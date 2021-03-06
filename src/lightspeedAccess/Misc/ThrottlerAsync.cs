﻿using System;
using System.Net;
using System.Threading.Tasks;
using lightspeedAccess.Misc;
using lightspeedAccess.Models.Common;
using Netco.ActionPolicyServices;
using System.IO;
using System.Runtime.ExceptionServices;

namespace LightspeedAccess.Misc
{
	public sealed class ThrottlerAsync
	{
		private readonly ThrottlingInfoItem _maxQuota;
		private readonly long _accountId;
		private readonly Func< Task > _delay;
		private readonly Func<Task> _delayOnThrottlingException;
		private readonly int _maxRetryCount;
		private readonly int _requestCost;
		private readonly ActionPolicyAsync _throttlerActionPolicy;

		private const int QuotaThreshold = 30;

		public ThrottlerAsync(ThrottlerConfig config)
		{
			this._maxQuota = config._maxQuota;
			this._delay = config._delay;
			this._maxRetryCount = config._maxRetryCount;
			this._accountId = config._accountId;
			this._requestCost = config._requestCost;
			this._delayOnThrottlingException = config._delayOnThrottlingException;

			this._throttlerActionPolicy = ActionPolicyAsync.Handle<Exception>().RetryAsync(this._maxRetryCount, async (ex, i) =>
			{
				if (this.IsExceptionFromThrottling(ex))
				 {
					 LightspeedLogger.Debug("Throttler: got throttling exception. Retrying...", (int)this._accountId);
					 await this._delayOnThrottlingException();
				 }
				 else
				 {
					 var errMessage = string.Format("Throttler: faced non-throttling exception: {0}", ex.Message);
					 LightspeedLogger.Debug(errMessage, (int)this._accountId);

					 var webException = ex as WebException;

					 if (webException != null)
					 {
						 var response = webException.Response as HttpWebResponse;
						 if (response != null)
						 {
							 if (response.StatusCode == HttpStatusCode.Unauthorized)
							 {
								 throw ex;
							 }

							 try
							 {
								 string responseText = this.SetResponseText(response, errMessage);

								 throw new LightspeedException(responseText, ex);

							 }
							 catch
							 {
								 throw new LightspeedException(errMessage, ex);
							 }
						 }
					 }

					 throw new LightspeedException(errMessage, ex);
				 }
			});
		}

		// default throttler that implements Lightspeed leaky bucket
		public ThrottlerAsync( long accountId ): this( ThrottlerConfig.CreateDefault( accountId ) )
		{
		}
 
		public async Task< TResult > ExecuteAsync< TResult >( Func< Task< TResult > > funcToThrottle )
		{
			try
			{
				return await this._throttlerActionPolicy.Get( () => this.TryExecuteAsync( funcToThrottle ) );
			}
			catch( AggregateException ex )
			{
				ExceptionDispatchInfo.Capture( ex.InnerException ).Throw();
				throw;
			}
		}

		private async Task< TResult > TryExecuteAsync< TResult >( Func< Task< TResult > > funcToThrottle )
		{
			var semaphore = LightspeedGlobalThrottlingInfo.GetSemaphoreSync( this._accountId );
			await semaphore.WaitAsync();

			await this.WaitIfNeededAsync();
			
			TResult result;
			try
			{
				result = await funcToThrottle();
				LightspeedLogger.Debug( "Throttler: request executed successfully", (int)this._accountId );
				this.SubtractQuota( result );
			}
			finally
			{
				semaphore.Release();
			}
			
			return result;
		}

		private bool IsExceptionFromThrottling( Exception exception )
		{
			var webException = exception as WebException;
			var response = webException != null ? webException.Response as HttpWebResponse : null;

			return response != null
                   && webException.Status == WebExceptionStatus.ProtocolError
                   && response.StatusCode == (HttpStatusCode)429;
        }

        private string SetResponseText(HttpWebResponse response, string errMessage)
        {
            string responseText;

            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                responseText = reader.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                responseText = errMessage;
            }

            return responseText;
        }

		private ThrottlingInfoItem GetRemainingQuota()
		{
			ThrottlingInfoItem info;
			if( !LightspeedGlobalThrottlingInfo.GetThrottlingInfo( this._accountId, out info ) )
				info = this._maxQuota;
			return info;
		}

		private void SetRemainingQuota( int quota, float dripRate )
		{
			LightspeedGlobalThrottlingInfo.AddThrottlingInfo( this._accountId, new ThrottlingInfoItem( quota, dripRate ) );
		}

		private async Task WaitIfNeededAsync()
		{
			var remainingQuota = this.GetRemainingQuota();
			LightspeedLogger.Debug(string.Format("Current quota remaining for account {0} is: {1}", this._accountId, remainingQuota.RemainingQuantity), (int)this._accountId );

			if( remainingQuota.RemainingQuantity > this._requestCost )
			{
				// we set new remaining quota for case potential error (this is strange, but it worked so long time)
				remainingQuota = new ThrottlingInfoItem( remainingQuota.RemainingQuantity - this._requestCost, remainingQuota.DripRate );
				this.SetRemainingQuota( remainingQuota.RemainingQuantity > 0 ? remainingQuota.RemainingQuantity : 0, remainingQuota.DripRate );
				return;
			}

			var secondsForDelay = Convert.ToInt32( Math.Ceiling( ( this._requestCost - remainingQuota.RemainingQuantity ) / remainingQuota.DripRate ) );
            var millisecondsForDelay = secondsForDelay * 1000;

			LightspeedLogger.Debug(string.Format("Throttler: quota exceeded. Waiting {0} seconds...", secondsForDelay), ( int )this._accountId );
			await Task.Delay(millisecondsForDelay);
			LightspeedLogger.Debug( "Throttler: Resuming...", (int)this._accountId );			
		}

		private void SubtractQuota< TResult >( TResult result )
		{
			LightspeedLogger.Debug( "Throttler: trying to get leaky bucket metadata from response", ( int )this._accountId );

			ResponseLeakyBucketMetadata bucketMetadata;
			if( QuotaParser.TryParseQuota( result, out bucketMetadata ) )
			{
				LightspeedLogger.Debug(string.Format("Throttler: parsed leaky bucket metadata from response. Bucket size: {0}. Used: {1}. Drip rate: {2}", bucketMetadata.quotaSize, bucketMetadata.quotaUsed, bucketMetadata.dripRate), ( int )this._accountId );
				var quotaDelta = bucketMetadata.quotaSize - bucketMetadata.quotaUsed;
				this.SetRemainingQuota( quotaDelta > 0 ? quotaDelta : 0, bucketMetadata.dripRate );
			}

			var remainingQuota = this.GetRemainingQuota();
			LightspeedLogger.Debug(string.Format("Throttler: subtracted quota, now available {0}, drip rate {1}", remainingQuota.RemainingQuantity, remainingQuota.DripRate), ( int )this._accountId );
		}

		public class ThrottlerException: Exception
		{
			public ThrottlerException()
			{
			}

			public ThrottlerException( string message )
				: base( message )
			{
			}

			public ThrottlerException( string message, Exception innerException )
				: base( message, innerException )
			{
			}
		}

		public class NonCriticalException: Exception
		{
			public NonCriticalException()
			{
			}

			public NonCriticalException( string message )
				: base( message )
			{
			}

			public NonCriticalException( string message, Exception innerException )
				: base( message, innerException )
			{
			}
		}
	}
}