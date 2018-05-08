using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleJsonService
{
    public interface IHostAJsonService
    {
        void Start();
        void Stop();
    }

    public interface INeedTheListenerContext
    {
        HttpListenerContext Context { get; set; }
    }

    public class JsonControllerBase : INeedTheListenerContext
    {
        public HttpListenerContext Context { get; set; }
    }

    public class JsonServiceHost : IHostAJsonService
    {
        private readonly Uri _baseUri;
        private readonly Type _controllerType;
        private readonly bool _quiet;
        private readonly CancellationTokenSource _tokenSource;
        private readonly Task _task;
        private readonly HttpListener _listener;
        private readonly IDictionary<string, MethodInfo> _routes;

        public JsonServiceHost(Type controllerType, string baseUrl, AuthenticationSchemes authentication, bool quiet)
        {
            _baseUri = new Uri(baseUrl);
            _tokenSource = new CancellationTokenSource();
            _task = new Task(Run, _tokenSource.Token, TaskCreationOptions.LongRunning);
            _listener = new HttpListener();
            _listener.Prefixes.Add(_baseUri.ToString());
            _listener.AuthenticationSchemes = authentication;
            _controllerType = controllerType;
            _quiet = quiet;
            _routes = new Dictionary<string, MethodInfo>();

            foreach (MethodInfo methodInfo in controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] paramsInfo = methodInfo.GetParameters();

                if (paramsInfo.Length > 1)
                    continue;

                _routes[methodInfo.Name.ToLower()] = methodInfo;
            }
        }

        public static IHostAJsonService Create<T>(string baseUrl, AuthenticationSchemes authentication = AuthenticationSchemes.Anonymous, bool quiet = false)
        {
            return new JsonServiceHost(typeof(T), baseUrl, authentication, quiet);
        }

        public void Start()
        {
            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                // If on Windows, and we get an access denied error, it's probably because we're missing a URL reservation for http.sys
                if (ex.ErrorCode == 5 && Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    string message = $"\nUnable to start listener on '{_baseUri}'. Ensure that the URL is reserved on this system.\n" + 
                                     "\n" + 
                                     $"    Example: netsh http add urlacl url={_baseUri.Scheme}://{_baseUri.Host}:{_baseUri.Port}{_baseUri.LocalPath} user=Everyone listen=yes\n\n";

                    if (_baseUri.Scheme == "https")
                    {
                        message = $"{message}" +
                                  $"Additionally, you must ensure that a certificate is bound to the port {_baseUri.Port}\n" +
                                  "\n" +
                                  $"    Example: netsh http add sslcert ipport=0.0.0.0:{_baseUri.Port} certhash=0123456789abcdef0123456789abcdef01234567 appid={{01234567-89ab-cdef-0123-456789abcdef}}\n" +
                                  "\n" +
                                  "'certhash' is the thumbprint of the certificate for this url.\n" +
                                  $"'appid' can be any GUID, but using your application GUID would be best.\n\n";
                    }

                    throw new ApplicationException(message, ex);
                }

                throw;
            }

            _task.Start();

            if (!_quiet)
            {
                Logger.Info($"Listening on {_baseUri}");

                foreach (string action in _routes.Keys)
                    Logger.Info($"    {_routes[action].ToActionDescription()}");
            }
        }

        public void Stop()
        {
            _listener.Stop();
            _tokenSource.Cancel();
            _task.Wait();
        }

        private async void Run()
        {
            while (!_tokenSource.IsCancellationRequested)
            {
                try
                {
                    ProcessContext(await _listener.GetContextAsync());
                }
                catch (HttpListenerException ex)
                {
                    if (ex.HResult != -2147467259 || ex.ErrorCode != 995)
                        throw;
                }
            }
        }

        private void ProcessContext(HttpListenerContext context)
        {
            using (var streamReader = new StreamReader(context.Request.InputStream))
            using (var streamWriter = new StreamWriter(context.Response.OutputStream))
            {
                string basePath = _baseUri.LocalPath;
                string contextPath = context.Request.Url.LocalPath;

                if (contextPath.Length <= basePath.Length)
                {
                    Logger.Warn($"No action found in URL: {contextPath}");
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                string action = context.Request.Url.LocalPath.Substring(_baseUri.LocalPath.Length).ToLower();

                if (!_routes.ContainsKey(action))
                {
                    Logger.Warn($"Action '{action}' not found in controller");
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                string jsonBody = streamReader.ReadToEnd();

                try
                {
                    MethodInfo methodInfo = _routes[action];
                    ParameterInfo paramInfo = methodInfo.GetParameters().FirstOrDefault();

                    object[] args = paramInfo == null 
                        ? new object[] { } 
                        : new[] { Serializer.DeserializeObject(jsonBody, paramInfo.ParameterType) };

                    object controller = ControllerFactory.CreateController(_controllerType);

                    if (controller is INeedTheListenerContext controllerWithContext)
                        controllerWithContext.Context = context;

                    object result = methodInfo.Invoke(controller, args);
                    string jsonResult = null;

                    if (result != null)
                        jsonResult = Serializer.SerializeObject(result);

                    if (jsonResult != null)
                        streamWriter.Write(jsonResult);

                    context.Response.StatusCode = (int) HttpStatusCode.OK;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error processing action '{action}': {ex.Message}");
                    Logger.Info("--");
                    Logger.Info(jsonBody);
                    Logger.Info("--");
                    context.Response.StatusCode = (int) HttpStatusCode.BadRequest;
                }
            }
        }

        public static class ControllerFactory
        {
            public static void Configure(Func<Type, object> createControllerFunc)
            {
                CreateController = createControllerFunc;
            }

            public static Func<Type,object> CreateController = Activator.CreateInstance;
        }

        public static class Serializer
        {
            public static void Configure(
                Func<string, Type, object> deserializeObjectFunc,
                Func<object, string> serializeObjectFunc)
            {
                DeserializeObject = deserializeObjectFunc;
                SerializeObject = serializeObjectFunc;
            }

            public static Func<string, Type, object> DeserializeObject = (json, type) =>
            {
                var serializer = new DataContractJsonSerializer(type);
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                using (var stream = new MemoryStream(jsonBytes))
                    return serializer.ReadObject(stream);
            };

            public static Func<object, string> SerializeObject = obj =>
            {
                var serializer = new DataContractJsonSerializer(obj.GetType());
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, obj);
                    return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                }
            };

            public static object Deserialize(string json, Type type)
            {
                return DeserializeObject(json, type);
            }

            public static string Serialize(object obj)
            {
                return SerializeObject(obj);
            }
        }

        public static class Logger
        {
            public static void Configure(
                Action<string, Exception> logInfoAction,
                Action<string, Exception> logWarnAction,
                Action<string, Exception> logErrorAction)
            {
                LogInfo = logInfoAction;
                LogWarn = logWarnAction;
                LogInfo = logInfoAction;
            }

            public static Action<string, Exception> LogInfo = (message, ex) =>
            {
                Write("INFO", message, ex);
            };

            public static Action<string, Exception> LogWarn = (message, ex) =>
            {
                Write("WARN", message, ex);
            };

            public static Action<string, Exception> LogError = (message, ex) =>
            {
                Write("ERROR", message, ex);
            };

            public static void Info(string message, Exception ex = null)
            {
                LogInfo(message, ex);
            }

            public static void Warn(string message, Exception ex = null)
            {
                LogWarn(message, ex);
            }

            public static void Error(string message, Exception ex = null)
            {
                LogError(message, ex);
            }

            private static void Write(string severity, string message, Exception ex)
            {
                Console.WriteLine($"{severity}\t{message}");

                if (ex != null)
                    Console.WriteLine(ex);
            }
        }
    }

    public static class MethodInfoExtensions
    {
        public static string ToActionDescription(this MethodInfo methodInfo)
        {
            var desc = new StringBuilder(methodInfo.Name.ToLower());

            ParameterInfo firstParam = methodInfo.GetParameters().FirstOrDefault();

            if (firstParam != null)
                desc.Append($" ({firstParam.ParameterType})");

            if (methodInfo.ReturnType != typeof(void))
                desc.Append($" -> {methodInfo.ReturnType}");

            return desc.ToString();
        }
    }
}
