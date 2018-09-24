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
            if (Env.GetEnvironmentBool("COMPUTE_RUN_SPLITBRAIN", false))
            {
                BackendPort = Env.GetEnvironmentString("COMPUTE_BACKEND_PORT", "");
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
                x.SetDisplayName(ServiceName);
                x.SetServiceName(ServiceName);
            });
            RhinoLib.ExitInProcess();
        }

        public static string ServiceName {
            get
            {
                if (IsRunningAsProxy)
                    return "RhinoCommon Front-End Server";
                else
                    return "RhinoCommon Geometry Server";
            }
        }

        public static bool IsRunningAsProxy { get; set; } = false;

        public static bool PerformFrontendFunctions { get; set; } = true;

        public static bool PerformBackendFunctions { get; set; } = true;

        public static string BackendPort { get; set; }
    }

    public class NancySelfHost
    {
        private NancyHost _nancyHost;
        private System.Diagnostics.Process _backendProcess = null;
        public static bool RunningHttps { get; set; }

        public void Start(int http_port, int https_port)
        {
            Logger.Init();
            if (Program.IsRunningAsProxy)
            {
                SpawnBackendProcess();
            }
            else
            {
                Logger.Info(null, $"Launching RhinoCore library as {Environment.UserName}");
                RhinoLib.LaunchInProcess(RhinoLib.LoadMode.Headless, 0);
            }
            var config = new HostConfiguration();
#if DEBUG
            config.RewriteLocalhost = false;  // Don't require URL registration for localhost when debugging
#endif
            var listenUriList = new List<Uri>();

            if (http_port > 0)
                listenUriList.Add(new Uri($"http://localhost:{http_port}"));
            if (https_port > 0)
                listenUriList.Add(new Uri($"https://localhost:{https_port}"));

            if (listenUriList.Count > 0)
                _nancyHost = new NancyHost(config, listenUriList.ToArray());
            else
                Logger.Error(null, "Neither http_port nor https_port are set; NOT LISTENING!");
            try
            {
                _nancyHost.Start();
                foreach (var uri in listenUriList)
                    Logger.Info(null, $"{Program.ServiceName}: running on {uri.OriginalString}");
            }
            catch (Nancy.Hosting.Self.AutomaticUrlReservationCreationFailureException)
            {
                Logger.Error(null, Environment.NewLine + "URL Not Reserved. From an elevated command promt, run:" + Environment.NewLine);
                foreach (var uri in listenUriList)
                    Logger.Error(null, $"netsh http add urlacl url={uri.Scheme}://+:{uri.Port}/ user=Everyone");
                Environment.Exit(1);
            }
        }

        private void SpawnBackendProcess()
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
            info.Environment.Remove("COMPUTE_RUN_SPLITBRAIN");
            info.Environment.Add("COMPUTE_RUNNING_AS_BACKEND", "1");
            info.Environment.Add("COMPUTE_HTTP_PORT", Program.BackendPort);
            info.Environment.Add("COMPUTE_LOG_SUFFIX", "-backend");

            info.FileName = System.Reflection.Assembly.GetEntryAssembly().Location;

            Logger.Info(null, $"Starting back-end geometry service on port {Program.BackendPort}");
            _backendProcess = System.Diagnostics.Process.Start(info);
            _backendProcess.EnableRaisingEvents = true;
            _backendProcess.Exited += _backendProcess_Exited;
        }

        private void _backendProcess_Exited(object sender, EventArgs e)
        {
            var process = sender as System.Diagnostics.Process;
            if (process?.ExitCode == -1)
                return;  // Process is closing from Ctrl+C on console

            _backendProcess = null;
            SpawnBackendProcess();
        }

        public void Stop()
        {
            if (_backendProcess != null)
                _backendProcess.Kill();
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
                return;

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
