﻿using System;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Threading;
using ServiceStack.Logging;
using ServiceStack.Text;
using ServiceStack.Web;

namespace ServiceStack.Host.HttpListener
{
    public delegate void DelReceiveWebRequest(HttpListenerContext context);

    /// <summary>
    /// Wrapper class for the HTTPListener to allow easier access to the
    /// server, for start and stop management and event routing of the actual
    /// inbound requests.
    /// </summary>
    public abstract class HttpListenerBase : ServiceStackHost
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(HttpListenerBase));

        private const int RequestThreadAbortedException = 995;

        protected System.Net.HttpListener Listener;
        protected bool IsStarted = false;
        protected string registeredReservedUrl = null;

        private readonly AutoResetEvent ListenForNextRequest = new AutoResetEvent(false);

        public event DelReceiveWebRequest ReceiveWebRequest;

        protected HttpListenerBase(string serviceName, params Assembly[] assembliesWithServices)
            : base(serviceName, assembliesWithServices) {}

        public virtual void OnAfterInit()
        {
            SetAppDomainData();
        }

        public virtual void SetAppDomainData()
        {
            //Required for Mono to resolve VirtualPathUtility and Url.Content urls
            var domain = Thread.GetDomain(); // or AppDomain.Current
            domain.SetData(".appDomain", "1");
            domain.SetData(".appVPath", "/");
            domain.SetData(".appPath", domain.BaseDirectory);
            if (string.IsNullOrEmpty(domain.GetData(".appId") as string))
            {
                domain.SetData(".appId", "1");
            }
            if (string.IsNullOrEmpty(domain.GetData(".domainId") as string))
            {
                domain.SetData(".domainId", "1");
            }
        }

        public override ServiceStackHost Start(string listeningAtUrlBase)
        {
            Start(listeningAtUrlBase, Listen);
            return this;
        }

        /// <summary>
        /// Starts the Web Service
        /// </summary>
        /// <param name="listeningAtUrlBase">
        /// A Uri that acts as the base that the server is listening on.
        /// Format should be: http://127.0.0.1:8080/ or http://127.0.0.1:8080/somevirtual/
        /// Note: the trailing slash is required! For more info see the
        /// HttpListener.Prefixes property on MSDN.
        /// </param>
        protected void Start(string listeningAtUrlBase, WaitCallback listenCallback)
        {
            // *** Already running - just leave it in place
            if (this.IsStarted)
                return;

            if (this.Listener == null)
                Listener = new System.Net.HttpListener();

            HostContext.Config.ServiceStackHandlerFactoryPath = ListenerRequest.GetHandlerPathIfAny(listeningAtUrlBase);

            Listener.Prefixes.Add(listeningAtUrlBase);

            IsStarted = true;

            try
            {
                Listener.Start();
            }
            catch (HttpListenerException ex)
            {
                if (Config.AllowAclUrlReservation && ex.ErrorCode == 5 && registeredReservedUrl == null)
                {
                    registeredReservedUrl = AddUrlReservationToAcl(listeningAtUrlBase);
                    if (registeredReservedUrl != null)
                    {
                        Start(listeningAtUrlBase, listenCallback);
                        return;
                    }
                }

                throw ex;
            }

            ThreadPool.QueueUserWorkItem(listenCallback);
        }

        private bool IsListening
        {
            get { return this.IsStarted && this.Listener != null && this.Listener.IsListening; }
        }

        // Loop here to begin processing of new requests.
        private void Listen(object state)
        {
            while (IsListening)
            {
                if (Listener == null) return;

                try
                {
                    Listener.BeginGetContext(ListenerCallback, Listener);
                    ListenForNextRequest.WaitOne();
                }
                catch (Exception ex)
                {
                    Log.Error("Listen()", ex);
                    return;
                }
                if (Listener == null) return;
            }
        }

        // Handle the processing of a request in here.
        private void ListenerCallback(IAsyncResult asyncResult)
        {
            var listener = asyncResult.AsyncState as System.Net.HttpListener;
            HttpListenerContext context = null;

            if (listener == null) return;

            try
            {
                if (!IsListening)
                {
                    Log.DebugFormat("Ignoring ListenerCallback() as HttpListener is no longer listening");
                    return;
                }
                // The EndGetContext() method, as with all Begin/End asynchronous methods in the .NET Framework,
                // blocks until there is a request to be processed or some type of data is available.
                context = listener.EndGetContext(asyncResult);
            }
            catch (Exception ex)
            {
                // You will get an exception when httpListener.Stop() is called
                // because there will be a thread stopped waiting on the .EndGetContext()
                // method, and again, that is just the way most Begin/End asynchronous
                // methods of the .NET Framework work.
                var errMsg = ex + ": " + IsListening;
                Log.Warn(errMsg);
                return;
            }
            finally
            {
                // Once we know we have a request (or exception), we signal the other thread
                // so that it calls the BeginGetContext() (or possibly exits if we're not
                // listening any more) method to start handling the next incoming request
                // while we continue to process this request on a different thread.
                ListenForNextRequest.Set();
            }

            if (context == null) return;

            Log.InfoFormat("{0} Request : {1}", context.Request.UserHostAddress, context.Request.RawUrl);

            //System.Diagnostics.Debug.WriteLine("Start: " + requestNumber + " at " + DateTime.UtcNow);
            //var request = context.Request;

            //if (request.HasEntityBody)

            RaiseReceiveWebRequest(context);

            try
            {
                this.ProcessRequest(context);
            }
            catch (Exception ex)
            {
                var error = string.Format("Error this.ProcessRequest(context): [{0}]: {1}", ex.GetType().Name, ex.Message);
                Log.ErrorFormat(error);

                HandleError(ex, context);
            }

            //System.Diagnostics.Debug.WriteLine("End: " + requestNumber + " at " + DateTime.UtcNow);
        }

        public static void HandleError(Exception ex, HttpListenerContext context)
        {
            try
            {
                var errorResponse = new ErrorResponse
                {
                    ResponseStatus = new ResponseStatus
                    {
                        ErrorCode = ex.GetType().Name,
                        Message = ex.Message,
                        StackTrace = ex.StackTrace,
                    }
                };

                var operationName = context.Request.GetOperationName();
                var httpReq = new ListenerRequest(operationName, context.Request);
                var httpRes = new ListenerResponse(context.Response);
                var requestCtx = new HttpRequestContext(httpReq, httpRes, errorResponse);
                var contentType = requestCtx.ResponseContentType;

                var serializer = HostContext.ContentTypes.GetResponseSerializer(contentType);
                if (serializer == null)
                {
                    contentType = HostContext.Config.DefaultContentType;
                    serializer = HostContext.ContentTypes.GetResponseSerializer(contentType);
                }

                var httpError = ex as IHttpError;
                if (httpError != null)
                {
                    httpRes.StatusCode = httpError.Status;
                    httpRes.StatusDescription = httpError.StatusDescription;
                }
                else
                {
                    httpRes.StatusCode = 500;
                }

                httpRes.ContentType = contentType;

                serializer(requestCtx, errorResponse, httpRes);

                httpRes.Close();
            }
            catch (Exception errorEx)
            {
                var error = "Error this.ProcessRequest(context)(Exception while writing error to the response): [{0}]: {1}"
                            .Fmt(errorEx.GetType().Name, errorEx.Message);
                Log.ErrorFormat(error);
            }
        }

        protected void RaiseReceiveWebRequest(HttpListenerContext context)
        {
            if (this.ReceiveWebRequest != null)
                this.ReceiveWebRequest(context);
        }


        /// <summary>
        /// Shut down the Web Service
        /// </summary>
        public virtual void Stop()
        {
            if (Listener == null) return;

            try
            {
                this.Listener.Close();

                // remove Url Reservation if one was made
                if (registeredReservedUrl != null)
                {
                    RemoveUrlReservationFromAcl(registeredReservedUrl);
                    registeredReservedUrl = null;
                }
            }
            catch (HttpListenerException ex)
            {
                if (ex.ErrorCode != RequestThreadAbortedException) throw;

                Log.ErrorFormat("Swallowing HttpListenerException({0}) Thread exit or aborted request", RequestThreadAbortedException);
            }
            this.IsStarted = false;
            this.Listener = null;
        }

        /// <summary>
        /// Overridable method that can be used to implement a custom hnandler
        /// </summary>
        /// <param name="context"></param>
        protected abstract void ProcessRequest(HttpListenerContext context);

        /// <summary>
        /// Reserves the specified URL for non-administrator users and accounts. 
        /// http://msdn.microsoft.com/en-us/library/windows/desktop/cc307223(v=vs.85).aspx
        /// </summary>
        /// <returns>Reserved Url if the process completes successfully</returns>
        public static string AddUrlReservationToAcl(string urlBase)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return null;

            try
            {
                string cmd, args;

                // use HttpCfg for windows versions before Version 6.0, else use NetSH
                if (Environment.OSVersion.Version.Major < 6)
                {
                    var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                    cmd = "httpcfg";
                    args = string.Format(@"set urlacl /u {0} /a D:(A;;GX;;;""{1}"")", urlBase, sid);
                }
                else
                {
                    cmd = "netsh";
                    args = string.Format(@"http add urlacl url={0} user={1}\{2} listen=yes", urlBase, Environment.UserDomainName, Environment.UserName);
                }

                var psi = new ProcessStartInfo(cmd, args)
                {
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };

                Process.Start(psi).WaitForExit();

                return urlBase;
            }
            catch
            {
                return null;
            }
        }

        public static void RemoveUrlReservationFromAcl(string urlBase)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return;

            try
            {

                string cmd, args;

                if (Environment.OSVersion.Version.Major < 6)
                {
                    cmd = "httpcfg";
                    args = string.Format(@"delete urlacl /u {0}", urlBase);
                }
                else
                {
                    cmd = "netsh";
                    args = string.Format(@"http delete urlacl url={0}", urlBase);
                }

                var psi = new ProcessStartInfo(cmd, args)
                {
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };

                Process.Start(psi).WaitForExit();
            }
            catch
            {
                /* ignore */
            }
        }

        private bool disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (disposed) return;
            base.Dispose();

            lock (this)
            {
                if (disposed) return;

                if (disposing)
                {
                    this.Stop();
                }

                //release unmanaged resources here...
                disposed = true;
            }
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
