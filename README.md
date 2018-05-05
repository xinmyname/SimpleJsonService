# SimpleJsonService
Very simple JSON REST API Service for HttpListener and .NET Standard 2.0

You can replace the logging and JSON serialization components

    JsonServiceHost.Logger.Configure(log.Info, log.Warn, log.Error);
    JsonServiceHost.Serializer.Configure(JsonConvert.DeserializeObject, JsonConvert.SerializeObject);
