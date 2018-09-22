using System;
using Nancy.Hosting.Self;
using Nancy.Extensions;
using Topshelf;
using Nancy.Conventions;
using Nancy.Bootstrapper;
using Nancy.TinyIoc;
using Nancy.Gzip;
using System.Collections.Generic;
using RhinoCommon.Rest.Authentication;
using System.Net.Http;

namespace RhinoCommon.Rest
{
    class Program
    {
        static void Main(string[] args)
        {
            // You may need to configure the Windows Namespace reservation to assign
            // rights to use the port that you set below.
            // See: https://github.com/NancyFx/Nancy/wiki/Self-Hosting-Nancy
            // Use cmd.exe or PowerShell in Administrator mode with the following command:
            // netsh http add urlacl url=http://+:80/ user=Everyone
            // netsh http add urlacl url=https://+:443/ user=Everyone
            int https_port = Env.GetEnvironmentInt("COMPUTE_HTTPS_PORT", 0);
#if DEBUG
            int http_port = Env.GetEnvironmentInt("COMPUTE_HTTP_PORT", 8888);
#else
            int http_port = Env.GetEnvironmentInt("COMPUTE_HTTP_PORT", 80);
#endif

            if (Env.GetEnvironmentBool("COMPUTE_RUNNING_AS_BACKEND", false))
                PerformFrontendFunctions = false;
            if (Env.GetEnvironmentString("COMPUTE_BACKEND_PORT", "") != "")
            {
                PerformBackendFunctions = false;
                IsRunningAsProxy = true;
            }

            Topshelf.HostFactory.Run(x =>
            {
                x.ApplyCommandLine();
                x.SetStartTimeout(new TimeSpan(0, 1, 0));
                x.Service<NancySelfHost>(s =>
                  {
                      s.ConstructUsing(name => new NancySelfHost());
                      s.WhenStarted(tc => tc.Start(http_port, https_port));
                      s.WhenStopped(tc => tc.Stop());
                  });
                x.RunAsPrompt();
                //x.RunAsLocalService();
                x.SetDisplayName("RhinoCommon Geometry Server");
                x.SetServiceName("RhinoCommon Geometry Server");
            });
            RhinoLib.ExitInProcess();
        }

        public static bool IsRunningAsProxy { get; set; } = false;

        public static bool PerformFrontendFunctions { get; set; } = true;

        public static bool PerformBackendFunctions { get; set; } = true;
    }

    public class NancySelfHost
    {
        private NancyHost _nancyHost;
        public static bool RunningHttps { get; set; }

        public void Start(int http_port, int https_port)
        {
            Logger.Init();
            if (Program.IsRunningAsProxy)
            {
                // Set up compute to run on a secondary process
                // Proxy requests through to the backend process.

                var info = new System.Diagnostics.ProcessStartInfo();
                info.UseShellExecute = false;
                foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    info.Environment.Add((string)entry.Key, (string)entry.Value);
                }
                info.Environment.Remove("COMPUTE_BACKEND_PORT");
                info.Environment.Add("COMPUTE_RUNNING_AS_BACKEND", "1");
                info.Environment.Add("COMPUTE_HTTP_PORT", Env.GetEnvironmentString("COMPUTE_BACKEND_PORT", ""));
                info.Environment.Add("COMPUTE_LOG_SUFFIX", "-backend");
                info.CreateNoWindow = false;

                info.FileName = System.Reflection.Assembly.GetEntryAssembly().Location;

                System.Diagnostics.Process.Start(info);
            }
            else
            {
                Logger.Info(null, $"Launching RhinoCore library as {Environment.UserName}");
                RhinoLib.LaunchInProcess(RhinoLib.LoadMode.Headless, 0);
            }
            var config = new HostConfiguration();
            var listenUriList = new List<Uri>();

            if (http_port > 0)
                listenUriList.Add(new Uri($"http://localhost:{http_port}"));
            if (https_port > 0)
                listenUriList.Add(new Uri($"https://localhost:{https_port}"));

            if (listenUriList.Count > 0)
                _nancyHost = new NancyHost(config, listenUriList.ToArray());
            else
                Logger.Info(null, "ERROR: neither http_port nor https_port are set; NOT LISTENING!");
            try
            {
                _nancyHost.Start();
                foreach (var uri in listenUriList)
                    Logger.Info(null, $"Running on {uri.OriginalString}");
            }
            catch (Nancy.Hosting.Self.AutomaticUrlReservationCreationFailureException)
            {
                Logger.Error(null, Environment.NewLine + "URL Not Reserved. From an elevated command promt, run:" + Environment.NewLine);
                foreach (var uri in listenUriList)
                    Logger.Error(null, $"netsh http add urlacl url={uri.Scheme}://+:{uri.Port}/ user=Everyone");
                Environment.Exit(1);
            }
        }

        public void Stop()
        {
            _nancyHost.Stop();
        }
    }

    public class Bootstrapper : Nancy.DefaultNancyBootstrapper
    {
        private byte[] _favicon;

        protected override void ApplicationStartup(TinyIoCContainer container, IPipelines pipelines)
        {
            Logger.Debug(null, "ApplicationStartup");
            pipelines.AddRequestId();

            if (Program.PerformFrontendFunctions)
            {
                pipelines.EnableGzipCompression(new GzipCompressionSettings() { MinimumBytes = 1024 });

                if (Env.GetEnvironmentBool("COMPUTE_AUTH_RHINOACCOUNT", false))
                    pipelines.AddAuthRhinoAccount();
                pipelines.AddRequestStashing();
                if (Env.GetEnvironmentBool("COMPUTE_AUTH_APIKEY", false))
                    pipelines.AddAuthApiKey();

            }
            base.ApplicationStartup(container, pipelines);
        }

        protected override void ConfigureConventions(NancyConventions nancyConventions)
        {
            base.ConfigureConventions(nancyConventions);
            nancyConventions.StaticContentsConventions.Add(StaticContentConventionBuilder.AddDirectory("docs"));
        }

        protected override byte[] FavIcon
        {
            get { return _favicon ?? (_favicon = LoadFavIcon()); }
        }

        private byte[] LoadFavIcon()
        {
            using (var resourceStream = GetType().Assembly.GetManifestResourceStream("RhinoCommon.Rest.favicon.ico"))
            {
                var memoryStream = new System.IO.MemoryStream();
                resourceStream.CopyTo(memoryStream);
                return memoryStream.GetBuffer();
            }
        }
    }

    public class RhinoModule : Nancy.NancyModule
    {
        public RhinoModule()
        {
            if (Program.IsRunningAsProxy)
                SetupProxyEndpoints(Env.GetEnvironmentString("COMPUTE_BACKEND_PORT", ""));
            else
                SetupRhinoEndpoints();
        }

        public void SetupProxyEndpoints(string backendPort)
        {
            Get["/"] =
            Get["/{uri*}"] = _ =>
            {
                var url = (string)_.uri;
                var proxy_url = $"http://localhost:{backendPort}/{url}";
                var client = new HttpClient();
                foreach(var header in Context.Request.Headers)
                {
                    client.DefaultRequestHeaders.Add(header.Key, header.Value);
                }
                client.DefaultRequestHeaders.Add("x-compute-id", (string)Context.Items["x-compute-id"]);
                client.DefaultRequestHeaders.Add("x-compute-host", (string)Context.Items["x-compute-host"]);
                var proxy_response = client.GetAsync(proxy_url).Result;

                var response = (Nancy.Response)proxy_response.Content.ReadAsStringAsync().Result;
                foreach (var header in proxy_response.Headers)
                {
                    foreach (var value in header.Value)
                        response.Headers.Add(header.Key, value);
                }
                return response;
            };

            Post["/"] =
            Post["/{uri*}"] = _ =>
            {
                var url = (string)_.uri;
                var proxy_url = $"http://localhost:{backendPort}/{url}";
                var client = new HttpClient();
                foreach (var header in Context.Request.Headers)
                {
                    if (header.Key == "Content-Length")
                        continue;
                    if (header.Key == "Content-Type")
                        continue;
                    client.DefaultRequestHeaders.Add(header.Key, header.Value);
                }
                client.DefaultRequestHeaders.Add("x-compute-id", (string)Context.Items["x-compute-id"]);
                client.DefaultRequestHeaders.Add("x-compute-host", (string)Context.Items["x-compute-host"]);

                object o_content;
                StringContent content;
                if (Context.Items.TryGetValue("request-body", out o_content))
                    content = new StringContent(o_content as string);
                else
                    content = new StringContent(Context.Request.Body.AsString());

                Nancy.Response response = null;
                try
                {
                    var proxy_response = client.PostAsync(proxy_url, content).Result;
                    string responseBody = proxy_response.Content.ReadAsStringAsync().Result;
                    response = (Nancy.Response)responseBody;
                    response.StatusCode = (Nancy.HttpStatusCode)proxy_response.StatusCode;
                    foreach (var header in proxy_response.Headers)
                    {
                        foreach (var value in header.Value)
                            response.Headers.Add(header.Key, value);
                    }
                }
                catch (Exception ex)
                {
                    response = (Nancy.Response)ex.Message;
                    response.StatusCode = Nancy.HttpStatusCode.InternalServerError;
                }

                return response;
            };
        }

        public void SetupRhinoEndpoints()
        {
            Get["/healthcheck"] = _ => "healthy";

            var endpoints = EndPointDictionary.GetDictionary();
            foreach (var kv in endpoints)
            {
                Get[kv.Key] = _ =>
                {
                    if (NancySelfHost.RunningHttps && !Request.Url.IsSecure)
                    {
                        string url = Request.Url.ToString().Replace("http", "https");
                        return new Nancy.Responses.RedirectResponse(url, Nancy.Responses.RedirectResponse.RedirectType.Permanent);
                    }
                    var response = kv.Value.HandleGetAsResponse(Context);
                    if (response != null)
                        return response;
                    return kv.Value.HandleGet();
                };

                if (kv.Value is GetEndPoint)
                    continue;

                Post[kv.Key] = _ =>
                {
                    if (NancySelfHost.RunningHttps && !Request.Url.IsSecure)
                        return Nancy.HttpStatusCode.HttpVersionNotSupported;

                    // Stashing middleware may have already read the body
                    object requestBody = null;
                    string jsonString = null;
                    if (Context.Items.TryGetValue("request-body", out requestBody))
                        jsonString = requestBody as string;
                    else
                        jsonString = Request.Body.AsString();

                    var resp = new Nancy.Response();
                    resp.Contents = (e) =>
                    {
                        using (var sw = new System.IO.StreamWriter(e))
                        {
                            bool multiple = false;
                            Dictionary<string, string> returnModifiers = null;
                            foreach (string name in Request.Query)
                            {
                                if (name.StartsWith("return.", StringComparison.InvariantCultureIgnoreCase))
                                {
                                    if (returnModifiers == null)
                                        returnModifiers = new Dictionary<string, string>();
                                    string dataType = "Rhino.Geometry." + name.Substring("return.".Length);
                                    string items = Request.Query[name];
                                    returnModifiers[dataType] = items;
                                    continue;
                                }
                                if (name.Equals("multiple", StringComparison.InvariantCultureIgnoreCase))
                                    multiple = Request.Query[name];
                            }
                            var postResult = kv.Value.HandlePost(jsonString, multiple, returnModifiers);
                            sw.Write(postResult);
                            sw.Flush();
                        }
                    };
                    return resp;
                };
            }
        }
    }
}
