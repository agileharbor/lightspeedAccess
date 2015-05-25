﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace lightspeedAccess.Models.Configuration
{
	public class LightspeedConfig
	{
		public string Endpoint { get; private set; }
		public string Username { get; private set; }
		public string Password { get; private set; }
		public string ApiKey { get; private set;  }
		public static int DefaultTimeoutSeconds = 10;
		public readonly int TimeoutSeconds;

		public readonly string path = "https://api.merchantos.com/API/Account";

		public LightspeedConfig()
		{
			var testAccountID = 797;
			Endpoint = string.Format( "{0}/{1}/", path, testAccountID );
			Username = "imademo";
			Password = "thisismypass";
			TimeoutSeconds = DefaultTimeoutSeconds; 
		}

		public LightspeedConfig( int accountId, string apiKey )
		{
			Endpoint = string.Format( "{0}/{1}/", path, accountId );
			ApiKey = apiKey;
			TimeoutSeconds = DefaultTimeoutSeconds; 
		}

		public LightspeedConfig(int timeoutSeconds)
		{
			var testAccountID = 797;
			Endpoint = string.Format( "{0}/{1}/", path, testAccountID );
			Username = "imademo";
			Password = "thisismypass";
			TimeoutSeconds = timeoutSeconds;
		}

	}

}