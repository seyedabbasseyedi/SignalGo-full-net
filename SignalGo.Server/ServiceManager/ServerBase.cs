﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SignalGo.Server.DataTypes;
using SignalGo.Server.Helpers;
using SignalGo.Server.IO;
using SignalGo.Server.Models;
using SignalGo.Server.Settings;
using SignalGo.Shared;
using SignalGo.Shared.DataTypes;
using SignalGo.Shared.Events;
using SignalGo.Shared.Helpers;
using SignalGo.Shared.Http;
using SignalGo.Shared.IO;
using SignalGo.Shared.Log;
using SignalGo.Shared.Managers;
using SignalGo.Shared.Models;
using SignalGo.Shared.Security;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SignalGo.Server.ServiceManager
{
    public abstract class ServerBase : IDisposable
    {
        /// <summary>
        /// server is started or not
        /// </summary>
        public bool IsStarted { get; set; }

        /// <summary>
        /// calling method count if this is going to zero server can stop
        /// </summary>
        private volatile int _callingCount;
        public int CallingCount
        {
            get
            {
                return _callingCount;
            }
            private set
            {
                _callingCount = value;
                if (_callingCount == 0)
                    CallAfterFinishAction?.Invoke();
            }
        }

        public ProviderSetting ProviderSetting { get; set; } = new ProviderSetting();

        public Action OnServerDisconnectedAction { get; set; }
        public Action<Exception> OnServerInternalExceptionAction { get; set; }

        public Action<ClientInfo> OnConnectedClientAction { get; set; }
        public Action<ClientInfo> OnDisconnectedClientAction { get; set; }

        //internal ConcurrentDictionary<ClientInfo, SynchronizationContext> ClientDispatchers { get; set; } = new ConcurrentDictionary<ClientInfo, SynchronizationContext>();
        internal static HashMapDictionary<SynchronizationContext, ClientInfo> AllDispatchers { get; set; } = new HashMapDictionary<SynchronizationContext, ClientInfo>();

        /// <summary>
        /// کلاینت ها و توابعی که منتظر هستند پاسخشون از سمت کلاینت برگرده
        /// </summary>
        internal ConcurrentDictionary<ClientInfo, ConcurrentDictionary<string, KeyValue<AutoResetEvent, MethodCallbackInfo>>> WaitedMethodsForResponse { get; set; } = new ConcurrentDictionary<ClientInfo, ConcurrentDictionary<string, KeyValue<AutoResetEvent, MethodCallbackInfo>>>();
        /// <summary>
        /// کلاینت ها
        /// </summary>
        internal ConcurrentDictionary<string, ClientInfo> Clients { get; set; } = new ConcurrentDictionary<string, ClientInfo>();
        internal ConcurrentDictionary<string, List<ClientInfo>> ClientsByIp { get; set; } = new ConcurrentDictionary<string, List<ClientInfo>>();

        /// <summary>
        /// settings of clients
        /// </summary>
        internal ConcurrentDictionary<ClientInfo, SecuritySettingsInfo> ClientsSettings { get; set; } = new ConcurrentDictionary<ClientInfo, SecuritySettingsInfo>();

        //public List<ClientInfo> AllClients
        //{
        //    get
        //    {
        //        return Clients.ToList();
        //    }
        //}

        /// <summary>
        /// کلاس هایی که سرور باید اونارو به عنوان سرویس بشناسه تا کلاینت ها بتونن برای خودشون بسازنش
        /// </summary>
        internal ConcurrentDictionary<string, Type> RegisteredServiceTypes { get; set; } = new ConcurrentDictionary<string, Type>();
        /// <summary>
        /// service types those can supp signalGo http requests,so this 
        /// </summary>
        internal ConcurrentDictionary<string, Type> RegisteredHttpServiceTypes { get; set; } = new ConcurrentDictionary<string, Type>();
        /// <summary>
        /// کلاس هایی که سرور اونارو میسازه تا بتونه توابع کلاینت رو صدا بزنه
        /// </summary>
        internal ConcurrentDictionary<string, Type> RegisteredCallbacksTypes { get; set; } = new ConcurrentDictionary<string, Type>();
        /// <summary>
        /// کلاس های توابع کلاینت که سرور انها را صدا میزند و به دست کاربر میرسد.
        /// </summary>
        internal ConcurrentDictionary<ClientInfo, ConcurrentList<object>> Services { get; set; } = new ConcurrentDictionary<ClientInfo, ConcurrentList<object>>();
        /// <summary>
        /// stream services for upload and download files
        /// </summary>
        internal ConcurrentDictionary<string, object> StreamServices { get; set; } = new ConcurrentDictionary<string, object>();

        /// <summary>
        /// signle instance services
        /// </summary>
        internal ConcurrentDictionary<string, object> SingleInstanceServices { get; set; } = new ConcurrentDictionary<string, object>();
        internal ConcurrentDictionary<ClientInfo, ConcurrentList<object>> Callbacks { get; set; } = new ConcurrentDictionary<ClientInfo, ConcurrentList<object>>();

        /// <summary>
        /// توابع و سرویس هایی که برای کلاینت رجیستر شده تا سرور اونارو صدا بزنه و بارگزاری داده نداشته باشه
        /// توی دیکشنری استرینگ اول اسم سرویس هست و استرینگ دوم لیست توابع هست
        /// </summary>
        internal ConcurrentDictionary<ClientInfo, ConcurrentDictionary<string, ConcurrentList<string>>> ClientRegistredMethods { get; set; } = new ConcurrentDictionary<ClientInfo, ConcurrentDictionary<string, ConcurrentList<string>>>();

        internal ConcurrentList<string> VirtualDirectories { get; set; } = new ConcurrentList<string>();

        internal SegmentManager CurrentSegmentManager = new SegmentManager();
        TcpListener server = null;
        Thread mainThread = null;
        /// <summary>
        /// اتصال به سرور
        /// </summary>
        /// <param name="serverUrl"></param>
        /// <param name="port"></param>
        /// <param name="virtualUrl"></param>
        internal void Connect(int port, string[] virtualUrl)
        {
            Exception exception = null;
            AutoResetEvent resetEvent = new AutoResetEvent(false);
            mainThread = new Thread(() =>
            {
                try
                {
                    server = new TcpListener(IPAddress.IPv6Any, port);
#if (NET35)
                    //server.Server.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IP, false);
#else
                    server.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
#endif
                    server.Server.NoDelay = true;

                    //server = new TcpListener(IPAddress.Parse(serverUrl), port);
                    foreach (var item in virtualUrl)
                    {
                        if (!VirtualDirectories.Contains(item))
                            VirtualDirectories.Add(item);
                    }
                    server.Start();
                    IsStarted = true;
                    resetEvent.Set();
                    while (true)
                    {
                        AcceptTcpClient();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Server Disposed! : " + ex);
                    OnServerInternalExceptionAction?.Invoke(ex);
                    SignalGo.Shared.Log.AutoLogger.LogError(ex, "Connect Server");
                    exception = ex;
                    resetEvent.Set();
                    Stop();
                }
            })
            {
                IsBackground = false
            };
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
#else
            mainThread.SetApartmentState(ApartmentState.STA);
#endif
            mainThread.Start();
            resetEvent.WaitOne();
            if (exception != null)
                throw exception;
        }
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
        internal async void AcceptTcpClient()
#else
        internal void AcceptTcpClient()
#endif
        {
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
            AddClient(await server.AcceptTcpClientAsync());
#else
            AddClient(server.AcceptTcpClient());
#endif
        }

        public void InitializeService(Type serviceType)
        {
            var name = serviceType.GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault().Name;
            RegisteredServiceTypes.TryAdd(name, serviceType);
        }

        public void InitializeService<T>()
        {
            InitializeService(typeof(T));
        }

        internal object FindClientServiceByType(ClientInfo client, Type serviceType, ServiceContractAttribute attribute)
        {
            if (attribute == null)
            {
                attribute = serviceType.GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault();
            }

            if (attribute.InstanceType == InstanceType.SingleInstance)
            {
                if (!SingleInstanceServices.TryGetValue(attribute.Name, out object result))
                {
                    if (ProviderSetting.AutoDetectRegisterServices)
                        RegisterServiceForClient(attribute, client);
                    SingleInstanceServices.TryGetValue(attribute.Name, out result);
                }
                return result;
            }
            if (!Services.ContainsKey(client))
                return null;
            var serviceName = attribute.Name;
            foreach (var item in Services[client])
            {
                if (serviceName == item.GetType().GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault().Name)
                    return item;
            }
            if (ProviderSetting.AutoDetectRegisterServices)
            {
                return RegisterServiceForClient(attribute, client);
            }
            return null;
        }

        internal object FindStreamServiceByName(string name)
        {
            StreamServices.TryGetValue(name, out object value);
            return value;
        }

        internal object FindClientCallbackByType(ClientInfo client, Type serviceType)
        {
            var serviceName = serviceType.GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault().Name;
            if (!Callbacks.ContainsKey(client))
            {
                AutoLogger.LogText($"Callbacks is not ContainsKey! {serviceType.FullName} {client.ClientId} {DateTime.Now}");
                return null;
            }
            foreach (var item in Callbacks[client].ToArray())
            {
                if (serviceName == item.GetType().GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault().Name)
                    return item;
            }
            return null;
        }

        public void RegisterClientService(Type type)
        {
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
            var name = ((ServiceContractAttribute)type.GetTypeInfo().GetCustomAttributes(typeof(ServiceContractAttribute), true).FirstOrDefault()).Name;
#else
            var name = ((ServiceContractAttribute)type.GetCustomAttributes(typeof(ServiceContractAttribute), true).FirstOrDefault()).Name;
#endif
            RegisteredCallbacksTypes.TryAdd(name, type);
        }
        /// <summary>
        /// register stream service for download and upload stream or file
        /// </summary>
        /// <param name="type"></param>
        public void RegisterStreamService(Type type)
        {
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
            var name = ((ServiceContractAttribute)AttributeHelper.GetCustomAttributes<ServiceContractAttribute>(type, true).FirstOrDefault()).Name;
#else
            var name = ((ServiceContractAttribute)type.GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault()).Name;
#endif
            if (StreamServices.ContainsKey(name))
                throw new Exception("duplicate call");
            var service = Activator.CreateInstance(type);
            StreamServices.TryAdd(name, service);
        }
        /// <summary>
        /// register stream service for download and upload stream or file
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void RegisterStreamService<T>()
        {
            RegisterStreamService(typeof(T));
        }
#if (!NETSTANDARD1_6 && !NETCOREAPP1_1)
        public void RegisterClientCallbackInterfaceService<T>()
        {
            Type type = typeof(T);
            var name = type.GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault().Name;// (().GetCustomAttributes(typeof(ServiceContractAttribute), true).FirstOrDefault()).Name;
            var obj = CSCodeInjection.GenerateInterfaceType(type, typeof(OperationCalls), new List<Type>() { typeof(ServiceContractAttribute), this.GetType() }, true);
            RegisteredCallbacksTypes.TryAdd(name, obj);
        }
#endif

        internal Type GetRegisteredCallbacksTypeByName(string name)
        {
            return RegisteredCallbacksTypes[name];
        }

        static volatile int _ClientConnectedCallingCount = 1;
        public static int ClientConnectedCallingCount
        {
            get
            {
                return _ClientConnectedCallingCount;
            }
            set
            {
                _ClientConnectedCallingCount = value;
            }
        }
        //#if (NET45 || NET35 || NET4)
        //        public static bool ValidateServerCertificate(
        //              object sender,
        //              System.Security.Cryptography.X509Certificates.X509Certificate certificate,
        //              System.Security.Cryptography.X509Certificates.X509Chain chain,
        //              System.Net.Security.SslPolicyErrors sslPolicyErrors)
        //        {
        //            return true;
        //            if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
        //                return true;

        //            Console.WriteLine("Certificate error: {0}", sslPolicyErrors);

        //            // Do not allow this client to communicate with unauthenticated servers.
        //            return false;
        //        }
        //        static System.Security.Cryptography.X509Certificates.X509Certificate2 GetRandomCertificate()
        //        {
        //            System.Security.Cryptography.X509Certificates.X509Store st = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
        //            st.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
        //            try
        //            {
        //                var certCollection = st.Certificates;

        //                if (certCollection.Count == 0)
        //                {
        //                    return null;
        //                }
        //                return certCollection[0];
        //            }
        //            finally
        //            {
        //                st.Close();
        //            }
        //        }
        //#endif
        /// <summary>
        /// when client connected to server
        /// </summary>
        /// <param name="tcpClient">client</param>
        private void AddClient(TcpClient tcpClient)
        {
            if (IsFinishingServer)
            {
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                tcpClient.Dispose();
#else
                tcpClient.Close();
#endif
                return;
            }

            AsyncActions.Run(() =>
            {
                string headerResponse = "";
                ClientInfo client = new ClientInfo();
                try
                {
                    client.ConnectedDateTime = DateTime.Now.ToLocalTime();
                    client.ServerBase = this;
                    client.TcpClient = tcpClient;
                    client.IPAddress = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString().Replace("::ffff:", "");
                    client.ClientId = Guid.NewGuid().ToString() + "-" + Guid.NewGuid().ToString();
                    AutoLogger.LogText($"client connected : {client.IPAddress} {client.ClientId} {DateTime.Now.ToString()}");
                    Console.WriteLine($"client connected : {client.IPAddress} {client.ClientId} {DateTime.Now.ToString()} {ClientConnectedCallingCount}");
                    ClientConnectedCallingCount++;
                    Clients.TryAdd(client.ClientId, client);
                    if (ClientsByIp.ContainsKey(client.IPAddress))
                        ClientsByIp[client.IPAddress].Add(client);
                    else
                        ClientsByIp.TryAdd(client.IPAddress, new List<Models.ClientInfo>() { client });
                    //Services.TryAdd(client, new ConcurrentList<object>());
                    WaitedMethodsForResponse.TryAdd(client, new ConcurrentDictionary<string, KeyValue<AutoResetEvent, MethodCallbackInfo>>());
                    ClientRegistredMethods.TryAdd(client, new ConcurrentDictionary<string, ConcurrentList<string>>());
                    //#if (NET45 || NET35 || NET4)
                    //                    System.Net.Security.SslStream sslStream = new System.Net.Security.SslStream(
                    //                tcpClient.GetStream(), false);
                    //                    //sslStream.AuthenticateAsServer(new System.Security.Cryptography.X509Certificates.X509Certificate2(@"D:\Github\websockets\websocket-sharp\ConsoleTest\bin\Debug\ConsoleTest_TemporaryKey.pfx", "3773284")
                    //                    //    , false, System.Security.Authentication.SslProtocols.Tls, false);
                    //                    sslStream.AuthenticateAsClient(server.LocalEndpoint.ToString());
                    //                    var sslBytes = new byte[1027 * 1024];
                    //                    var sslReadCount = sslStream.Read(sslBytes, 0, sslBytes.Length);
                    //                    var ttttttt = System.Text.Encoding.UTF8.GetString(sslBytes, 0, sslReadCount);
                    //#endif
                    using (var reader = new CustomStreamReader(tcpClient.GetStream()))
                    {
                        headerResponse = reader.ReadLine();
                        if (headerResponse.Contains("SignalGo-Stream/2.0"))
                        {
                            //"SignalGo/1.0";
                            //"SignalGo/1.0";
                            client.IsWebSocket = false;
                            var b = GoStreamReader.ReadOneByte(tcpClient.GetStream(), CompressMode.None, 1, false);
                            if (SynchronizationContext.Current == null)
                                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                            //ClientDispatchers.TryAdd(client, SynchronizationContext.Current);
                            AllDispatchers.Add(SynchronizationContext.Current, client);
                            client.MainContext = SynchronizationContext.Current;
                            client.MainThread = System.Threading.Thread.CurrentThread;
                            //upload from client and download from server
                            if (b == 0)
                            {
                                DownloadStreamFromClient(tcpClient.GetStream(), client);
                            }
                            //download from server and upload from client
                            else
                            {
                                UploadStreamToClient(tcpClient.GetStream(), client);
                            }
                            DisposeClient(client, "AddClient end signalgo stream");
                            return;
                        }
                        else if (headerResponse.Contains("SignalGo/1.0"))
                        {
                            //"SignalGo/1.0";
                            //"SignalGo/1.0";
                            client.IsWebSocket = false;
                            var bytes = System.Text.Encoding.UTF8.GetBytes("OK");
                            tcpClient.GetStream().Write(bytes, 0, bytes.Length);
                        }
                        else if (headerResponse.Contains("HTTP/1.1"))
                        {
                            while (true)
                            {
                                var line = reader.ReadLine();
                                headerResponse += line;
                                if (line == "\r\n")
                                    break;
                            }
                            if (headerResponse.Contains("Sec-WebSocket-Key"))
                            {
                                //Console.WriteLine($"WebSocket client detected : {client.IPAddress} {client.ClientId} {DateTime.Now.ToString()} {ClientConnectedCallingCount}");

                                client.IsWebSocket = true;
                                var key = headerResponse.Replace("ey:", "`").Split('`')[1].Replace("\r", "").Split('\n')[0].Trim();
                                var acceptKey = AcceptKey(ref key);
                                var newLine = "\r\n";

                                //var response = "HTTP/1.1 101 Switching Protocols" + newLine
                                var response = "HTTP/1.0 101 Switching Protocols" + newLine
                                     + "Upgrade: websocket" + newLine
                                     + "Connection: Upgrade" + newLine
                                     + "Sec-WebSocket-Accept: " + acceptKey + newLine + newLine;
                                var bytes = System.Text.Encoding.UTF8.GetBytes(response);
                                tcpClient.GetStream().Write(bytes, 0, bytes.Length);
                                //Console.WriteLine($"WebSocket client send reponse success size:{bytes.Length} sended{count}");
                            }
                            else
                            {

                                if (SynchronizationContext.Current == null)
                                    SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                                AllDispatchers.Add(SynchronizationContext.Current, client);

                                string[] lines = null;
                                if (headerResponse.Contains("\r\n\r\n"))
                                    lines = headerResponse.Substring(0, headerResponse.IndexOf("\r\n\r\n")).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                else
                                    lines = headerResponse.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                                if (lines.Length > 0)
                                {
                                    var methodName = GetHttpMethodName(lines[0]);
                                    var address = GetHttpAddress(lines[0]);
                                    if (methodName.ToLower() == "get" && !string.IsNullOrEmpty(address) && address != "/")
                                    {
                                        var headers = GetHttpHeaders(lines.Skip(1).ToArray());
                                        RunHttpRequest(address, "GET", "", headers, client);
                                        DisposeClient(client, "AddClient finish get call");
                                        return;
                                    }
                                    else if (methodName.ToLower() == "post" && !string.IsNullOrEmpty(address) && address != "/")
                                    {
                                        var indexOfStartedContent = headerResponse.IndexOf("\r\n\r\n");
                                        string content = "";
                                        if (indexOfStartedContent > 0)
                                        {
                                            indexOfStartedContent += 4;
                                            content = headerResponse.Substring(indexOfStartedContent, headerResponse.Length - indexOfStartedContent);
                                        }
                                        var headers = GetHttpHeaders(lines.Skip(1).ToArray());
                                        if (headers["content-type"] != null && headers["content-type"].ToLower().Contains("multipart/form-data"))
                                        {
                                            RunPostHttpRequestFile(address, "POST", content, headers, client);
                                        }
                                        else
                                        {
                                            RunHttpRequest(address, "POST", content, headers, client);
                                        }
                                        DisposeClient(client, "AddClient finish post call");
                                        return;
                                    }
                                }

                                var newLine = "\r\n";

                                var response = "HTTP/1.1 200 OK" + newLine
                                     + "Content-Type: text/html" + newLine
                                     + "Connection: Close" + newLine;
                                tcpClient.Client.Send(System.Text.Encoding.ASCII.GetBytes(response + newLine + "SignalGo Server OK" + newLine));
                                DisposeClient(client, "AddClient http ok signalGo");
                                return;
                            }

                        }
                        else
                        {
                            if (headerResponse == null)
                                headerResponse = "";
                            AutoLogger.LogText($"Header not suport msg: {headerResponse} {client.IPAddress} IsConnected:{client.TcpClient.Connected} LastByte:{reader.LastByteRead}");

                            DisposeClient(client, "AddClient header not support");
                            return;
                        }

                        StartToReadingClientData(client);
                        OnConnectedClientAction?.Invoke(client);
                    }
                }
                catch (Exception ex)
                {
                    if (headerResponse == null)
                        headerResponse = "";
                    AutoLogger.LogText($"AddClient Error msg : {headerResponse} {client.IPAddress}");
                    AutoLogger.LogError(ex, "AddClient");
                    Console.WriteLine(ex);
                    DisposeClient(client, "AddClient exception");
                }
            });
        }

        /// <summary>
        /// server internal Settings
        /// </summary>
        public InternalSetting InternalSetting { get; set; } = new InternalSetting();

        #region Http request supports
        /// <summary>
        /// Http protocol and response request settings 
        /// </summary>
        public HttpProtocolSetting HttpProtocolSetting { get; set; } = new HttpProtocolSetting() { HandleCrossOriginAccess = true };

        /// <summary>
        /// get method name of http response
        /// </summary>
        /// <param name="reponse">response string</param>
        /// <returns>method name like "GET"</returns>
        private string GetHttpMethodName(string reponse)
        {
            var lines = reponse.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
                return lines[0];
            return "";
        }

        /// <summary>
        /// get http address from response
        /// </summary>
        /// <param name="reponse">response string</param>
        /// <returns>address</returns>
        private string GetHttpAddress(string reponse)
        {
            var lines = reponse.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 1)
                return lines[1];
            return "";
        }

        /// <summary>
        /// get http header from response
        /// </summary>
        /// <param name="lines">lines of headers</param>
        /// <returns>http headers</returns>
        private Shared.Http.WebHeaderCollection GetHttpHeaders(string[] lines)
        {
            Shared.Http.WebHeaderCollection result = new Shared.Http.WebHeaderCollection();
            foreach (var item in lines)
            {
                var keyValues = item.Split(new[] { ':' }, 2);
                if (keyValues.Length > 1)
                {
                    result.Add(keyValues[0], keyValues[1].TrimStart());
                }
            }
            return result;
        }

        /// <summary>
        /// run method of server http class with address and headers
        /// </summary>
        /// <param name="address">address</param>
        /// <param name="headers">headers</param>
        /// <param name="client">client</param>
        private void RunHttpRequest(string address, string httpMethod, string content, Shared.Http.WebHeaderCollection headers, ClientInfo client)
        {
            var newLine = "\r\n";
            Action<string> sendInternalErrorMessage = (data) =>
            {
                try
                {
                    //{ 500} {HttpRequestController.GetStatusDescription((int)HttpStatusCode.InternalServerError)}
                    string settingHeaders = "";
                    if (HttpProtocolSetting != null)
                    {
                        if (HttpProtocolSetting.HandleCrossOriginAccess)
                        {
                            settingHeaders = "Access-Control-Allow-Origin: " + headers["origin"] + newLine;
                        }
                    }
                    string message = newLine + $"Internal Server Error: {data}" + newLine;
                    var response = $"HTTP/1.1 {(int)HttpStatusCode.InternalServerError} {HttpRequestController.GetStatusDescription((int)HttpStatusCode.InternalServerError)}" + newLine
                        + "Content-Type: text/html; charset=utf-8" + newLine
                        + settingHeaders +
                        "Content-Length: " + (message.Length - 2) + newLine
                        + "Connection: Close" + newLine;
                    client.TcpClient.Client.Send(System.Text.Encoding.UTF8.GetBytes(response + message));
                }
                catch (Exception ex)
                {
                    AutoLogger.LogError(ex, "RunHttpGETRequest sendErrorMessage");
                }
            };
            string fullAddress = address;
            address = address.Trim('/');
            var lines = address.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (lines.Count <= 1)
            {
                string data = newLine + "SignalGo Error: method not found from address: " + address + newLine;
                sendInternalErrorMessage(data);
                AutoLogger.LogText(data);
            }
            else
            {
                var methodName = lines.Last();
                string parameters = "";
                Dictionary<string, string> multiPartParameter = new Dictionary<string, string>();
                if (httpMethod == "GET")
                {
                    if (methodName.Contains("?"))
                    {
                        var sp = methodName.Split(new[] { '?' }, 2);
                        methodName = sp.First();
                        parameters = sp.Last();
                    }
                }
                else if (httpMethod == "POST")
                {
                    var len = int.Parse(headers["content-length"]);
                    if (content.Length < len)
                    {
                        byte[] buffer = new byte[len - content.Length];
                        var readCount = client.TcpClient.Client.Receive(buffer);
                        var postResponse = Encoding.UTF8.GetString(buffer.ToList().GetRange(0, readCount).ToArray());
                        content = postResponse;

                    }

                    methodName = lines.Last();
                    parameters = content;
                    if (methodName.Contains("?"))
                    {
                        var sp = methodName.Split(new[] { '?' }, 2);
                        methodName = sp.First();
                        parameters = sp.Last();
                    }
                    else if (parameters.StartsWith("----") && parameters.ToLower().Contains("content-disposition"))
                    {
                        var boundary = parameters.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)[0];
                        var pValues = parameters.Split(new string[] { boundary }, StringSplitOptions.RemoveEmptyEntries);
                        string name = "";
                        foreach (var data in pValues)
                        {
                            if (data.ToLower().Contains("content-disposition"))
                            {
                                if (data.Replace(" ", "").Contains(";name="))
                                {
                                    int index = data.ToLower().IndexOf("content-disposition");
                                    var header = data.Substring(index);
                                    var headLen = header.IndexOf("\r\n");
                                    header = data.Substring(index, headLen);
                                    var newData = data.Substring(index + headLen + 2);
                                    //newData = newData.Split(new string[] { boundary }, StringSplitOptions.RemoveEmptyEntries);
                                    if (header.ToLower().IndexOf("content-disposition:") == 0)
                                    {
                                        var disp = new CustomContentDisposition(header);
                                        if (disp.Parameters.ContainsKey("name"))
                                            name = disp.Parameters["name"];
                                        newData = newData.Substring(2, newData.Length - 4);
                                        multiPartParameter.Add(name, newData);
                                    }
                                }
                            }
                        }
                    }
                }


                methodName = methodName.ToLower();
                lines.RemoveAt(lines.Count - 1);
                address = "";
                foreach (var item in lines)
                {
                    address += item + "/";
                }
                address = address.TrimEnd('/').ToLower();
                if (RegisteredHttpServiceTypes.ContainsKey(address))
                {
                    ActionResult result = null;
                    MethodInfo method = null;
                    List<string> valueitems = null;
                    Exception exception = null;
                    string callGuid = Guid.NewGuid().ToString();
                    HttpRequestController service = null;
                    try
                    {
                        ClientConnectedCallingCount++;
                        var methods = (from x in RegisteredHttpServiceTypes[address].GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance) where x.Name.ToLower() == methodName && x.IsPublic && !(x.IsSpecialName && (x.Name.StartsWith("set_") || x.Name.StartsWith("get_"))) select x).ToList();
                        if (methods.Count == 0)
                        {
                            string data = newLine + "SignalGo Error: Method name not found in method list: " + methodName + newLine;
                            sendInternalErrorMessage(data);
                            AutoLogger.LogText(data);
                            return;
                        }

                        List<Tuple<string, string>> values = new List<Tuple<string, string>>();
                        if (multiPartParameter.Count > 0)
                        {
                            foreach (var item in multiPartParameter)
                            {
                                values.Add(new Tuple<string, string>(item.Key, item.Value));
                            }
                        }
                        else if (headers["content-type"] == "application/json")
                        {
                            JObject des = (JObject)ServerSerializationHelper.Deserialize(parameters, this);
                            foreach (var item in des.Properties())
                            {
                                var value = des.GetValue(item.Name);
                                values.Add(new Tuple<string, string>(item.Name, Uri.UnescapeDataString(value.Value<string>())));
                            }
                        }
                        else
                        {
                            parameters = parameters.Trim('&');
                            if (!string.IsNullOrEmpty(parameters))
                            {
                                foreach (var item in parameters.Split(new[] { '&' }))
                                {
                                    var keyValue = item.Split(new[] { '=' }, 2);
                                    values.Add(new Tuple<string, string>(keyValue.Length == 2 ? keyValue[0] : "", Uri.UnescapeDataString(keyValue.Last())));
                                }
                            }
                            //                            if (!string.IsNullOrEmpty(content))
                            //                            {
                            //#if (NET35)
                            //#else
                            //                                content = System.Net.WebUtility.HtmlDecode(content);
                            //#endif
                            //                            }
                        }


                        method = (from x in methods where x.GetParameters().Length == values.Count select x).FirstOrDefault();


                        if (method == null)
                        {
                            string data = newLine + "SignalGo Error: Method name not found: " + methodName + newLine;
                            sendInternalErrorMessage(data);
                            AutoLogger.LogText(data);
                            return;
                        }

                        var clientLimitationAttribute = method.GetCustomAttributes(typeof(ClientLimitationAttribute), true).ToList();

                        foreach (ClientLimitationAttribute attrib in clientLimitationAttribute)
                        {
                            var allowAddresses = attrib.GetAllowAccessIpAddresses();
                            if (allowAddresses != null && allowAddresses.Length > 0)
                            {
                                if (!allowAddresses.Contains(client.IPAddress))
                                {
                                    string data = newLine + $"Client IP Have Not Access To Call Method: {client.IPAddress}" + newLine;
                                    sendInternalErrorMessage(data);
                                    AutoLogger.LogText(data);
                                    return;
                                }
                            }
                            else
                            {
                                var denyAddresses = attrib.GetDenyAccessIpAddresses();
                                if (denyAddresses != null && denyAddresses.Length > 0)
                                {
                                    if (denyAddresses.Contains(client.IPAddress))
                                    {
                                        string data = newLine + $"Client IP Is Deny Access To Call Method: {client.IPAddress}" + newLine;
                                        sendInternalErrorMessage(data);
                                        AutoLogger.LogText(data);
                                        return;
                                    }
                                }
                            }
                        }


                        var prms = method.GetParameters();
                        List<object> resultParameters = new List<object>();
                        var noParamNameDetected = (from x in values where string.IsNullOrEmpty(x.Item1) select x).Count() > 0;

                        int index = 0;
                        foreach (var item in prms)
                        {
                            Tuple<string, string> currentParam = null;
                            if (!noParamNameDetected)
                            {
                                currentParam = (from x in values where x.Item1.ToLower() == item.Name.ToLower() select x).FirstOrDefault();
                                if (currentParam == null)
                                {
                                    string data = newLine + $"result from method {methodName}, parameter {item.Name} not exist, your params {content} " + newLine;
                                    sendInternalErrorMessage(data);
                                    AutoLogger.LogText("RunHttpGETRequest : " + data);
                                    return;
                                }
                            }
                            else
                                currentParam = values[index];
                            if (string.IsNullOrEmpty(currentParam.Item2))
                                resultParameters.Add(GetDefault(item.ParameterType));
                            else
                            {
                                var obj = ServerSerializationHelper.DeserializeByValidate(currentParam.Item2, item.ParameterType, this);


                                //if (obj == null)
                                //    obj = ServerSerializationHelper.Deserialize(currentParam.Item2.SerializeObject(this), item.ParameterType, this);
                                resultParameters.Add(obj);
                            }
                            index++;
                        }
                        valueitems = values.Select(x => x.Item2).ToList();
                        MethodsCallHandler.BeginHttpMethodCallAction?.Invoke(client, callGuid, address, method, valueitems);
                        service = (HttpRequestController)Activator.CreateInstance(RegisteredHttpServiceTypes[address]);
                        service.RequestHeaders = headers;
                        service.ClientIpAddress = client.IPAddress;
                        if (HttpProtocolSetting != null)
                        {
                            if (HttpProtocolSetting.HandleCrossOriginAccess)
                            {
                                service.ResponseHeaders.Add("Access-Control-Allow-Origin", headers["origin"]);
                            }
                        }

                        var securityAttributes = method.GetCustomAttributes(typeof(SecurityContractAttribute), true).ToList();
                        foreach (SecurityContractAttribute attrib in securityAttributes)
                        {
                            if (!attrib.CheckHttpPermission(client, service, address, methodName, fullAddress, resultParameters))
                            {
                                result = attrib.GetHttpValueWhenDenyPermission(client, service, address, methodName, fullAddress, resultParameters);
                                if (result == null)
                                {
                                    string data = newLine + $"result from method invoke {methodName}, is null or is not ActionResult type" + address + newLine;
                                    sendInternalErrorMessage(data);
                                    AutoLogger.LogText("RunHttpGETRequest : " + data);
                                }
                                else
                                {
                                    RunHttpActionResult(service, result, client.TcpClient);
                                }
                                return;
                            }
                        }

                        bool isStaticLock = method.GetCustomAttributes(typeof(StaticLockAttribute), true).Count() > 0;
                        if (isStaticLock)
                        {
                            lock (StaticLockObject)
                            {
                                result = method.Invoke(service, resultParameters.ToArray()).ToActionResult();
                            }
                        }
                        else
                            result = method.Invoke(service, resultParameters.ToArray()).ToActionResult();

                        if (result == null)
                        {
                            string data = newLine + $"result from method invoke {methodName}, is null " + address + newLine;
                            sendInternalErrorMessage(data);
                            AutoLogger.LogText("RunHttpGETRequest : " + data);
                        }
                        else
                        {
                            RunHttpActionResult(service, result, client.TcpClient);
                        }
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                        if (HTTPErrorHandlingFunction != null)
                        {
                            result = HTTPErrorHandlingFunction(ex).ToActionResult();
                            RunHttpActionResult(service, result, client.TcpClient);
                        }
                        else
                        {
                            string data = newLine + ex.ToString() + address + newLine;
                            sendInternalErrorMessage(data);
                        }
                        AutoLogger.LogError(ex, "RunHttpRequest");
                    }
                    finally
                    {
                        ClientConnectedCallingCount--;
                        MethodsCallHandler.EndHttpMethodCallAction?.Invoke(client, callGuid, address, method, valueitems, result, exception);
                    }
                }
                else
                {
                    string data = newLine + "SignalGo Error: address not found in signalGo services: " + address + newLine;
                    sendInternalErrorMessage(data);
                    AutoLogger.LogText(data);
                }
            }
        }

        /// <summary>
        /// run method of server http class with address and headers
        /// </summary>
        /// <param name="address">address</param>
        /// <param name="headers">headers</param>
        /// <param name="client">client</param>
        private void RunPostHttpRequestFile(string address, string httpMethod, string content, Shared.Http.WebHeaderCollection headers, ClientInfo client)
        {
            var newLine = "\r\n";
            Action<string> sendInternalErrorMessage = (data) =>
            {
                try
                {
                    //{ 500} {HttpRequestController.GetStatusDescription((int)HttpStatusCode.InternalServerError)}
                    string settingHeaders = "";
                    if (HttpProtocolSetting != null)
                    {
                        if (HttpProtocolSetting.HandleCrossOriginAccess)
                        {
                            settingHeaders = "Access-Control-Allow-Origin: " + headers["origin"] + newLine;
                        }
                    }
                    string message = newLine + $"Internal Server Error: {data}" + newLine;

                    var response = $"HTTP/1.1 {(int)HttpStatusCode.InternalServerError} {HttpRequestController.GetStatusDescription((int)HttpStatusCode.InternalServerError)}" + newLine
                                    + "Content-Type: text/html; charset=utf-8" + newLine
                                    + settingHeaders +
                                    "Content-Length: " + (message.Length - 2) + newLine
                                    + "Connection: Close" + newLine;
                    Console.WriteLine(response + message);
                    client.TcpClient.Client.Send(System.Text.Encoding.UTF8.GetBytes(response + message));
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    AutoLogger.LogError(ex, "RunHttpGETRequest sendErrorMessage");
                }
            };
            string fullAddress = address;
            address = address.Trim('/');
            var lines = address.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (lines.Count <= 1)
            {
                string data = newLine + "SignalGo Error: method not found from address: " + address + newLine;
                sendInternalErrorMessage(data);
                AutoLogger.LogText(data);
            }
            else
            {
                var methodName = lines.Last();
                string parameters = "";
                if (methodName.Contains("?"))
                {
                    var sp = methodName.Split(new[] { '?' }, 2);
                    methodName = sp.First();
                    parameters = sp.Last();
                }
                Dictionary<string, string> multiPartParameter = new Dictionary<string, string>();

                var len = int.Parse(headers["content-length"]);
                HttpPostedFileInfo fileInfo = null;
                if (content.Length < len)
                {
                    var boundary = headers["content-type"].Split('=').Last();
                    var fileHeaderCount = 0;
                    string response = "";
                    fileHeaderCount = GetHttpFileFileHeader(client.TcpClient.GetStream(), ref boundary, len, out response);
                    boundary = boundary.TrimStart('-');
                    string contentType = "";
                    string fileName = "";
                    string name = "";
                    bool findFile = false;
                    foreach (var data in response.Split(new string[] { "------" + boundary }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (data.ToLower().Contains("content-disposition"))
                        {
                            if (data.Replace(" ", "").Contains(";filename="))
                            {
                                foreach (var header in data.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
                                {
                                    var index = header.ToLower().IndexOf("content-type: ");
                                    if (index == 0)
                                    {
                                        var ctypeLen = "content-type: ".Length;
                                        contentType = header.Substring(ctypeLen, header.Length - ctypeLen);
                                    }
                                    else if (header.ToLower().IndexOf("content-disposition:") == 0)
                                    {
                                        var disp = new CustomContentDisposition(header);
                                        if (disp.Parameters.ContainsKey("filename"))
                                            fileName = disp.Parameters["filename"];
                                        if (disp.Parameters.ContainsKey("name"))
                                            name = disp.Parameters["name"];
                                    }
                                    findFile = true;
                                }
                                break;
                            }
                            else if (data.ToLower().Contains("content-disposition"))
                            {
                                if (data.Replace(" ", "").Contains(";name="))
                                {
                                    int index = data.ToLower().IndexOf("content-disposition");
                                    var header = data.Substring(index);
                                    var headLen = header.IndexOf("\r\n");
                                    header = data.Substring(index, headLen);
                                    //var byteData = GoStreamReader.ReadBlockSize(client.TcpClient.GetStream(), (ulong)(len - content.Length - fileHeaderCount));
                                    string newData = data.Substring(headLen + 4);//Encoding.UTF8.GetString(byteData);
                                    //var newData = text.Substring(0, text.IndexOf(boundary) - 4);
                                    if (header.ToLower().IndexOf("content-disposition:") == 0)
                                    {
                                        var disp = new CustomContentDisposition(header);
                                        if (disp.Parameters.ContainsKey("name"))
                                            name = disp.Parameters["name"];
                                        multiPartParameter.Add(name, newData);
                                    }
                                }
                            }
                            var keyValue = data.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                            if (keyValue.Length == 2)
                            {
                                if (!string.IsNullOrEmpty(parameters))
                                {
                                    parameters += "&";
                                }
                                var disp = new CustomContentDisposition(keyValue[0]);
                                foreach (var prm in disp.Parameters)
                                {
                                    parameters += prm.Key;
                                    parameters += "=" + prm.Value;
                                }

                            }
                        }
                    }
                    if (findFile)
                    {
                        var stream = new StreamGo(client.TcpClient.GetStream());
                        stream.SetOfStreamLength(len - content.Length - fileHeaderCount, boundary.Length + 12);// + 6
                        fileInfo = new HttpPostedFileInfo()
                        {
                            Name = name,
                            ContentLength = stream.Length,
                            ContentType = contentType,
                            FileName = fileName,
                            InputStream = stream
                        };
                    }


                    //byte[] buffer = new byte[len * 5];
                    //var readCount = client.TcpClient.Client.Receive(buffer);
                    //// I dont know why 44 bytes(overplus) always sent
                    //var postResponse = Encoding.UTF8.GetString(buffer.ToList().GetRange(0, readCount).ToArray());
                    //content = postResponse;
                }




                methodName = methodName.ToLower();
                lines.RemoveAt(lines.Count - 1);
                address = "";
                foreach (var item in lines)
                {
                    address += item + "/";
                }
                address = address.TrimEnd('/').ToLower();
                if (RegisteredHttpServiceTypes.ContainsKey(address))
                {
                    ActionResult result = null;
                    MethodInfo method = null;
                    List<string> valueitems = null;
                    Exception exception = null;
                    string callGuid = Guid.NewGuid().ToString();
                    HttpRequestController service = null;
                    try
                    {
                        ClientConnectedCallingCount++;
                        var methods = (from x in RegisteredHttpServiceTypes[address].GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance) where x.Name.ToLower() == methodName && x.IsPublic && !(x.IsSpecialName && (x.Name.StartsWith("set_") || x.Name.StartsWith("get_"))) select x).ToList();
                        if (methods.Count == 0)
                        {
                            string data = newLine + "SignalGo Error: Method name not found in method list: " + methodName + newLine;
                            sendInternalErrorMessage(data);
                            AutoLogger.LogText(data);
                            return;
                        }

                        List<Tuple<string, string>> values = new List<Tuple<string, string>>();
                        if (multiPartParameter.Count > 0)
                        {
                            foreach (var item in multiPartParameter)
                            {
                                values.Add(new Tuple<string, string>(item.Key, item.Value));
                            }
                        }
                        else if (headers["content-type"] == "application/json")
                        {
                            JObject des = (JObject)ServerSerializationHelper.Deserialize(parameters);
                            foreach (var item in des.Properties())
                            {
                                var value = des.GetValue(item.Name);
                                values.Add(new Tuple<string, string>(item.Name, Uri.UnescapeDataString(value.Value<string>())));
                            }
                        }
                        else
                        {
                            parameters = parameters.Trim('&');
                            if (!string.IsNullOrEmpty(parameters))
                            {
                                foreach (var item in parameters.Split(new[] { '&' }))
                                {
                                    var keyValue = item.Split(new[] { '=' }, 2);
                                    values.Add(new Tuple<string, string>(keyValue.Length == 2 ? keyValue[0] : "", Uri.UnescapeDataString(keyValue.Last())));
                                }
                            }
                        }


                        method = (from x in methods where x.GetParameters().Length == values.Count select x).FirstOrDefault();


                        if (method == null)
                        {
                            string data = newLine + "SignalGo Error: Method name not found: " + methodName + newLine;
                            sendInternalErrorMessage(data);
                            AutoLogger.LogText(data);
                            return;
                        }

                        var clientLimitationAttribute = method.GetCustomAttributes(typeof(ClientLimitationAttribute), true).ToList();

                        foreach (ClientLimitationAttribute attrib in clientLimitationAttribute)
                        {
                            var allowAddresses = attrib.GetAllowAccessIpAddresses();
                            if (allowAddresses != null && allowAddresses.Length > 0)
                            {
                                if (!allowAddresses.Contains(client.IPAddress))
                                {
                                    string data = newLine + $"Client IP Have Not Access To Call Method: {client.IPAddress}" + newLine;
                                    sendInternalErrorMessage(data);
                                    AutoLogger.LogText(data);
                                    return;
                                }
                            }
                            else
                            {
                                var denyAddresses = attrib.GetDenyAccessIpAddresses();
                                if (denyAddresses != null && denyAddresses.Length > 0)
                                {
                                    if (denyAddresses.Contains(client.IPAddress))
                                    {
                                        string data = newLine + $"Client IP Is Deny Access To Call Method: {client.IPAddress}" + newLine;
                                        sendInternalErrorMessage(data);
                                        AutoLogger.LogText(data);
                                        return;
                                    }
                                }
                            }
                        }

                        service = (HttpRequestController)Activator.CreateInstance(RegisteredHttpServiceTypes[address]);
                        service.RequestHeaders = headers;
                        service.ClientIpAddress = client.IPAddress;
                        service.SetFirstFile(fileInfo);
                        if (HttpProtocolSetting != null)
                        {
                            if (HttpProtocolSetting.HandleCrossOriginAccess)
                            {
                                service.ResponseHeaders.Add("Access-Control-Allow-Origin", headers["origin"]);
                            }
                        }


                        var prms = method.GetParameters();
                        List<object> resultParameters = new List<object>();
                        var noParamNameDetected = (from x in values where string.IsNullOrEmpty(x.Item1) select x).Count() > 0;

                        int index = 0;
                        foreach (var item in prms)
                        {
                            Tuple<string, string> currentParam = null;
                            if (!noParamNameDetected)
                            {
                                currentParam = (from x in values where x.Item1.ToLower() == item.Name.ToLower() select x).FirstOrDefault();
                                if (currentParam == null)
                                {
                                    string data = newLine + $"result from method {methodName}, parameter {item.Name} not exist, your params {content} " + newLine;
                                    sendInternalErrorMessage(data);
                                    AutoLogger.LogText("RunHttpGETRequest : " + data);
                                    return;
                                }
                            }
                            else
                                currentParam = values[index];
                            if (string.IsNullOrEmpty(currentParam.Item2))
                                resultParameters.Add(GetDefault(item.ParameterType));
                            else
                            {
                                var obj = ServerSerializationHelper.DeserializeByValidate(currentParam.Item2, item.ParameterType, this);
                                //if (obj == null)
                                //    obj = ServerSerializationHelper.Deserialize(currentParam.Item2.SerializeObject(this), item.ParameterType, this);
                                resultParameters.Add(obj);
                            }
                            index++;
                        }
                        valueitems = values.Select(x => x.Item2).ToList();
                        MethodsCallHandler.BeginHttpMethodCallAction?.Invoke(client, callGuid, address, method, valueitems);
                        var securityAttributes = method.GetCustomAttributes(typeof(SecurityContractAttribute), true).ToList();
                        foreach (SecurityContractAttribute attrib in securityAttributes)
                        {
                            if (!attrib.CheckHttpPermission(client, service, address, methodName, fullAddress, resultParameters))
                            {
                                result = attrib.GetHttpValueWhenDenyPermission(client, service, address, methodName, fullAddress, resultParameters);
                                if (result == null)
                                {
                                    string data = newLine + $"result from method invoke {methodName}, is null or is not ActionResult type" + address + newLine;
                                    sendInternalErrorMessage(data);
                                    AutoLogger.LogText("RunHttpGETRequest : " + data);
                                }
                                else
                                {
                                    RunHttpActionResult(service, result, client.TcpClient);
                                }
                                return;
                            }
                        }
                        bool isStaticLock = method.GetCustomAttributes(typeof(StaticLockAttribute), true).Count() > 0;
                        if (isStaticLock)
                        {
                            lock (StaticLockObject)
                            {
                                result = method.Invoke(service, resultParameters.ToArray()).ToActionResult();
                            }
                        }
                        else
                            result = method.Invoke(service, resultParameters.ToArray()).ToActionResult();

                        if (result == null)
                        {
                            string data = newLine + $"result from method invoke {methodName}, is null or is not ActionResult type" + address + newLine;
                            sendInternalErrorMessage(data);
                            AutoLogger.LogText("RunHttpGETRequest : " + data);
                        }
                        else
                        {
                            RunHttpActionResult(service, result, client.TcpClient);
                        }
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                        if (HTTPErrorHandlingFunction != null)
                        {
                            result = HTTPErrorHandlingFunction(ex).ToActionResult();
                            RunHttpActionResult(service, result, client.TcpClient);
                        }
                        else
                        {
                            string data = newLine + ex.ToString() + address + newLine;
                            sendInternalErrorMessage(data);
                        }
                        AutoLogger.LogError(ex, "RunPostHttpRequestFile");
                    }
                    finally
                    {
                        ClientConnectedCallingCount--;
                        MethodsCallHandler.EndHttpMethodCallAction?.Invoke(client, callGuid, address, method, valueitems, result, exception);
                    }
                }
                else
                {
                    string data = newLine + "SignalGo Error: address not found in signalGo services: " + address + newLine;
                    sendInternalErrorMessage(data);
                    AutoLogger.LogText(data);
                }
            }
        }

        int GetHttpFileFileHeader(Stream stream, ref string boundary, int maxLen, out string response)
        {
            List<byte> bytes = new List<byte>();
            byte findNextlvl = 0;
            while (true)
            {
                var singleByte = stream.ReadByte();
                if (singleByte < 0)
                    break;
                bytes.Add((byte)singleByte);
                if (bytes.Count >= maxLen)
                {
                    var data = Encoding.UTF8.GetString(bytes.ToArray());
                    response = data;
                    return bytes.Count;

                }
                if (findNextlvl > 0)
                {
                    if (findNextlvl == 1 && singleByte == 10)
                        findNextlvl++;
                    else if (findNextlvl == 2 && singleByte == 13)
                        findNextlvl++;
                    else if (findNextlvl == 3 && singleByte == 10)
                    {
                        var data = Encoding.UTF8.GetString(bytes.ToArray());
                        var res = data.Replace(" ", "").ToLower();

                        if (res.Contains("content-disposition:") && res.Contains("filename"))
                            break;
                        findNextlvl = 0;
                    }
                    else
                        findNextlvl = 0;
                }
                else
                {
                    if (singleByte == 13)
                        findNextlvl++;
                }
            }
            response = Encoding.UTF8.GetString(bytes.ToArray());
            if (response.Contains("--"))
            {
                var lastEnter = response.IndexOf("\r\n");
                if (lastEnter > 0)
                    boundary = response.Substring(response.LastIndexOf("--") + 2, lastEnter - 6);
            }
            return bytes.Count;
        }

        private void RunHttpActionResult(HttpRequestController controller, ActionResult result, TcpClient client)
        {
            var newLine = "\r\n";

            var response = $"HTTP/1.1 {(int)controller.Status} {HttpRequestController.GetStatusDescription((int)controller.Status)}" + newLine;

            //foreach (string item in headers)
            //{
            //    response += item + ": " + headers[item] + newLine;
            //}

            if (result is FileActionResult && controller.Status == HttpStatusCode.OK)
            {
                response += controller.ResponseHeaders.ToString();
                var file = (FileActionResult)result;
                long fileLength = -1;
                //string len = "";
                try
                {
                    fileLength = file.FileStream.Length;
                    //len = "Content-Length: " + fileLength;
                }
                catch { }
                //response += len + newLine;
                client.Client.Send(System.Text.Encoding.ASCII.GetBytes(response));
                List<byte> allb = new List<byte>();
                if (file.FileStream.CanSeek)
                    file.FileStream.Seek(0, System.IO.SeekOrigin.Begin);
                while (fileLength != file.FileStream.Position)
                {
                    byte[] data = new byte[1024 * 20];
                    var readCount = file.FileStream.Read(data, 0, data.Length);
                    if (readCount == 0)
                        break;
                    client.Client.Send(data.ToList().GetRange(0, readCount).ToArray());
                }
                file.FileStream.Dispose();
            }
            else
            {
                var data = (result.Data is string ? result.Data.ToString() : ServerSerializationHelper.SerializeObject(result.Data, this));
                var dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
                if (controller.ResponseHeaders["content-length"] == null)
                    controller.ResponseHeaders.Add("Content-Length", (dataBytes.Length).ToString());

                if (controller.ResponseHeaders["Content-Type"] == null)
                {
                    if (result.Data is string)
                        controller.ResponseHeaders.Add("Content-Type", "text/html; charset=utf-8");
                    else
                        controller.ResponseHeaders.Add("Content-Type", "application/json; charset=utf-8");
                }

                if (controller.ResponseHeaders["Connection"] == null)
                    controller.ResponseHeaders.Add("Connection", "close");

                response += controller.ResponseHeaders.ToString();

                client.Client.Send(System.Text.Encoding.UTF8.GetBytes(response));

                //response += "Content-Type: text/html" + newLine + "Connection: Close" + newLine;
                client.Client.Send(dataBytes);
                client.GetStream().Flush();
                Thread.Sleep(100);


            }
        }

        /// <summary>
        /// add a http service class
        /// </summary>
        /// <param name="httpService">a service class that inheritance HttpRequestController and using HttpSupport attribute</param>
        public void AddHttpService<T>()
        {
            AddHttpService(typeof(T));
        }

        /// <summary>
        /// add a http service class
        /// </summary>
        /// <param name="httpService">a service class that inheritance HttpRequestController and using HttpSupport attribute</param>
        public void AddHttpService(Type httpService)
        {
            var attributes = httpService.GetCustomAttributes<HttpSupportAttribute>();
            if (attributes == null || attributes.Length == 0)
                throw new Exception("HttpSupport attribute not found from: " + httpService.FullName);
            bool exist = false;
            foreach (var item in CSCodeInjection.GetListOfTypes(httpService))
            {
                if (item == typeof(HttpRequestController))
                {
                    exist = true;
                    break;
                }
            }

            if (!exist)
                throw new Exception("your type is not nested HttpRequestController: " + httpService.FullName);

            foreach (var attrib in attributes)
            {
                foreach (var address in attrib.Addresses)
                {
                    if (string.IsNullOrEmpty(address))
                    {
                        throw new Exception("HttpSupport Address is null or empty from: " + httpService.FullName);
                    }
                    else if (RegisteredHttpServiceTypes.ContainsKey(address.ToLower()))
                    {
                        throw new Exception($"HttpSupport Address {address} is exist please use another address for this class type: {httpService.FullName}");
                    }
                    else
                    {
                        RegisteredHttpServiceTypes.TryAdd(address.ToLower(), httpService);
                    }
                }
            }
        }

        #endregion


        /// <summary>
        /// guid for web socket client connection
        /// </summary>
        private string guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        /// <summary>
        /// accept key for websoket client
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        private string AcceptKey(ref string key)
        {
            string longKey = key + guid;
            byte[] hashBytes = ComputeHash(longKey);
            return Convert.ToBase64String(hashBytes);
        }

        static SHA1 sha1 = SHA1.Create();
        /// <summary>
        /// comput sha1 hash
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        private static byte[] ComputeHash(string str)
        {
            return sha1.ComputeHash(Encoding.ASCII.GetBytes(str));
        }

        /// <summary>
        /// register calbacks for client
        /// </summary>
        /// <param name="client"></param>
        internal void RegisterCallbacksForClient(ClientInfo client)
        {
            var callbacks = new ConcurrentList<object>();
            foreach (var item in RegisteredCallbacksTypes)
            {
                //callbacks.Add(OCExtension.GetClientCallbackOfClientContext(this, client, item.Value));
                var objectInstance = Activator.CreateInstance(item.Value);
                if (CSCodeInjection.InvokedServerMethodAction == null)
                    ServerExtension.Init();
                var field = item.Value
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                .GetTypeInfo()
#endif
                .GetProperty("InvokedServerMethodAction");

                field.SetValue(objectInstance, CSCodeInjection.InvokedServerMethodAction, null);

                var field2 = item.Value
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                .GetTypeInfo()
#endif
                .GetProperty("InvokedServerMethodFunction");

                field2.SetValue(objectInstance, CSCodeInjection.InvokedServerMethodFunction, null);
                //objectInstance.InvokedServerMethodAction = CSCodeInjection.InvokedServerMethodAction;
                //objectInstance.InvokedServerMethodFunction = CSCodeInjection.InvokedServerMethodFunction;

                var op = objectInstance as OperationCalls;
                op.ServerBase = this;
                op.CurrentClient = client;
                //objectInstance.ServerBase = this;
                callbacks.Add(objectInstance);
                AutoLogger.LogText($"RegisterCallbacksForClient foreach {((object)objectInstance).ToString()} {client.ClientId}");
                ////objectInstance.OnInitialized();
            }
            var add = Callbacks.TryAdd(client, callbacks);
            AutoLogger.LogText($"RegisterCallbacksForClient add {add} {client.ClientId}");
        }

        /// <summary>
        /// after client connected this method start to reading client requests from new thread
        /// </summary>
        /// <param name="client"></param>
        private void StartToReadingClientData(ClientInfo client)
        {
            Thread thread = null;
            thread = new Thread(() =>
            {
                //save SynchronizationContext for call methods from client thread
                if (SynchronizationContext.Current == null)
                    SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                //ClientDispatchers.TryAdd(client, SynchronizationContext.Current);
                AllDispatchers.Add(SynchronizationContext.Current, client);
                client.MainContext = SynchronizationContext.Current;
                client.MainThread = thread;
                try
                {
                    RegisterCallbacksForClient(client);
                    var stream = client.TcpClient.GetStream();
                    bool isVerify = false;
                    if (!client.IsVerification)
                    {
                        if (client.TcpClient.Connected)
                        {
                            var bytes = GoStreamReader.ReadBlockToEnd(stream, CompressMode.None, 2048, client.IsWebSocket);
                            var json = Encoding.UTF8.GetString(bytes);
                            List<string> registers = ServerSerializationHelper.Deserialize<List<string>>(json, this);
                            foreach (var item in registers)
                            {
                                if (VirtualDirectories.Contains(item) || item == "/DownloadFile" || item == "/UploadFile")
                                {
                                    isVerify = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (!isVerify)
                    {
                        Console.WriteLine($"client is not verify: {client.ClientId}");
                        DisposeClient(client, "StartToReadingClientData client is not verify");
                        return;
                    }
                    client.IsVerification = true;
                    while (client.TcpClient.Connected)
                    {
                        var oneByteOfDataType = GoStreamReader.ReadOneByte(stream, CompressMode.None, ProviderSetting.MaximumReceiveDataBlock, client.IsWebSocket);
                        //بایت اول نوع دیتا
                        var dataType = (DataType)oneByteOfDataType;
                        if (dataType == DataType.PingPong)
                        {
                            //AutoLogger.LogText($"PingPong {client.IsWebSocket} {client.SessionId} {client.IPAddress}");
                            GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), new byte[] { 5 }, client.IsWebSocket);
                            continue;
                        }
                        //بایت دوم نوع فشرده سازی
                        var compressMode = (CompressMode)GoStreamReader.ReadOneByte(stream, CompressMode.None, ProviderSetting.MaximumReceiveDataBlock, client.IsWebSocket);
                        //یکی از متد های سرور توسط این کلاینت صدا زده شده
                        if (dataType == DataType.CallMethod)
                        {
                            string json = "";
                            do
                            {
                                var bytes = GoStreamReader.ReadBlockToEnd(stream, compressMode, ProviderSetting.MaximumReceiveDataBlock, client.IsWebSocket);
                                if (ClientsSettings.ContainsKey(client))
                                    bytes = DecryptBytes(bytes, client);
                                json += Encoding.UTF8.GetString(bytes);
                            }
                            while (client.IsWebSocket && json.IndexOf("#end") != json.Length - 4);

                            if (client.IsWebSocket)
                            {
                                if (json.IndexOf("#end") == json.Length - 4)
                                    json = json.Substring(0, json.Length - 4);
                            }
                            MethodCallInfo callInfo = ServerSerializationHelper.Deserialize<MethodCallInfo>(json, this);
                            if (callInfo.PartNumber != 0)
                            {
                                var result = CurrentSegmentManager.GenerateAndMixSegments(callInfo);
                                if (result != null)
                                    callInfo = (MethodCallInfo)result;
                                else
                                    continue;
                            }
                            if (callInfo == null)
                                AutoLogger.LogText($"{client.IPAddress} {client.ClientId} callinfo is null:" + json);
                            //else
                            //    AutoLogger.LogText(callInfo.MethodName);
                            //ست کردن تنظیمات
                            if (callInfo.ServiceName == "/SetSettings")
                                SetSettings(ServerSerializationHelper.Deserialize<SecuritySettingsInfo>(callInfo.Data.ToString(), this), callInfo, client, json);
                            //بررسی آدرس اتصال
                            else if (callInfo.ServiceName == "/CheckConnection")
                                SendCallbackData(new MethodCallbackInfo() { Guid = callInfo.Guid, Data = ServerSerializationHelper.SerializeObject(true, this) }, client);
                            //کلاسی کالبکی که سمت سرور جدید میشه
                            else if (callInfo.MethodName == "/RegisterService")
                                CalculateRegisterServiceForClient(callInfo, client);
                            //متد هایی که لازمه برای کلاینت کال بشه
                            else if (callInfo.MethodName == "/RegisterClientMethods")
                            {
                                RegisterMethodsForClient(callInfo, client);
                            }
                            //حذف متد هایی که قبلا رجیستر شده بود
                            else if (callInfo.MethodName == "/UnRegisterClientMethods")
                            {
                                UnRegisterMethodsForClient(callInfo, client);
                            }
                            else
                                CallMethod(callInfo, client, json);
                        }
                        //پاسخ دریافت شده از صدا زدن یک متد از کلاینت توسط سرور
                        else if (dataType == DataType.ResponseCallMethod)
                        {
                            var bytes = GoStreamReader.ReadBlockToEnd(stream, compressMode, ProviderSetting.MaximumReceiveDataBlock, client.IsWebSocket);
                            if (ClientsSettings.ContainsKey(client))
                                bytes = DecryptBytes(bytes, client);
                            var json = Encoding.UTF8.GetString(bytes);
                            MethodCallbackInfo callback = ServerSerializationHelper.Deserialize<MethodCallbackInfo>(json, this);
                            if (callback == null)
                                AutoLogger.LogText($"{client.IPAddress} {client.ClientId} callback is null:" + json);
                            if (callback.PartNumber != 0)
                            {
                                var result = CurrentSegmentManager.GenerateAndMixSegments(callback);
                                if (result != null)
                                    callback = (MethodCallbackInfo)result;
                                else
                                    continue;
                            }
                            var geted = WaitedMethodsForResponse.TryGetValue(client, out ConcurrentDictionary<string, KeyValue<AutoResetEvent, MethodCallbackInfo>> keyValue);
                            if (geted)
                            {
                                if (keyValue.ContainsKey(callback.Guid))
                                {
                                    keyValue[callback.Guid].Value = callback;
                                    keyValue[callback.Guid].Key.Set();
                                }
                            }
                        }
                        else if (dataType == DataType.RegisterFileDownload)
                        {
                            using (var writeToClientStrem = RegisterFileToDownload(stream, compressMode, client, client.IsWebSocket))
                            {
                                WriteStreamToClient(writeToClientStrem, stream, client.IsWebSocket);
                            }
                            break;
                        }
                        else if (dataType == DataType.RegisterFileUpload)
                        {
                            RegisterFileToUpload(stream, compressMode, client, client.IsWebSocket);
                            break;
                        }
                        else if (dataType == DataType.GetServiceDetails)
                        {
                            var bytes = GoStreamReader.ReadBlockToEnd(stream, compressMode, ProviderSetting.MaximumReceiveDataBlock, client.IsWebSocket);
                            if (ClientsSettings.ContainsKey(client))
                                bytes = DecryptBytes(bytes, client);
                            var json = Encoding.UTF8.GetString(bytes);
                            var hostUrl = ServerSerializationHelper.Deserialize<string>(json, this);
                            SendServiceDetail(client, hostUrl);
                        }
                        else if (dataType == DataType.GetMethodParameterDetails)
                        {
                            var bytes = GoStreamReader.ReadBlockToEnd(stream, compressMode, ProviderSetting.MaximumReceiveDataBlock, client.IsWebSocket);
                            if (ClientsSettings.ContainsKey(client))
                                bytes = DecryptBytes(bytes, client);
                            var json = Encoding.UTF8.GetString(bytes);
                            var detail = ServerSerializationHelper.Deserialize<MethodParameterDetails>(json, this);
                            SendMethodParameterDetail(client, detail);
                        }
                        else if (dataType == DataType.GetClientId)
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(client.ClientId);
                            if (ClientsSettings.ContainsKey(client))
                                bytes = EncryptBytes(bytes, client);
                            byte[] len = BitConverter.GetBytes(bytes.Length);
                            List<byte> data = new List<byte>
                            {
                                (byte)DataType.GetClientId,
                                (byte)CompressMode.None
                            };
                            data.AddRange(len);
                            data.AddRange(bytes);
                            if (data.Count > ProviderSetting.MaximumSendDataBlock)
                                throw new Exception($"{client.IPAddress} {client.ClientId} GetClientId data length is upper than MaximumSendDataBlock");

                            GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), data.ToArray(), client.IsWebSocket);
                        }
                        else
                        {
                            //throw new Exception($"Correct DataType Data {dataType}");
                            AutoLogger.LogText($"Correct DataType Data {oneByteOfDataType} {client.ClientId} {client.IPAddress}");
                            break;
                        }
                    }
                    DisposeClient(client, "StartToReadingClientData while break");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                    SignalGo.Shared.Log.AutoLogger.LogError(ex, $"{client.IPAddress} {client.ClientId} ServerBase StartToReadingClientData");
                    DisposeClient(client, "StartToReadingClientData exception");
                }
            })
            {
                IsBackground = false
            };
            thread.Start();
            //AsyncActions.Run(() =>
            //{

            //});
        }

        public void CheckClient(ClientInfo client)
        {
            if (client != null && !client.TcpClient.Connected)
                DisposeClient(client, "CheckClient");
        }

        private void ClientRemove(ClientInfo client)
        {
            Clients.Remove(client.ClientId);
            if (ClientsByIp.ContainsKey(client.IPAddress))
            {
                ClientsByIp[client.IPAddress].Remove(client);
                if (ClientsByIp[client.IPAddress].Count == 0)
                    ClientsByIp.Remove(client.IPAddress);
            }

            WaitedMethodsForResponse.Remove(client);
            Services.Remove(client);
            ClientRegistredMethods.Remove(client);
            Callbacks.Remove(client);
        }

        internal void DisposeClient(ClientInfo client, string reason)
        {
            try
            {
                Console.WriteLine("Client disposed " + client.ClientId + " reason: " + reason);
                try
                {
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                    client.TcpClient.Dispose();
#else
                    client.TcpClient.Close();
#endif
                }
                catch (Exception ex)
                {
                    AutoLogger.LogError(ex, $"{client.IPAddress} {client.ClientId} CloseCllient");
                }
                ClientRemove(client);

                //List<SynchronizationContext> contexts = new List<SynchronizationContext>();
                //foreach (var item in AllDispatchers)
                //{
                //    if (item.Value == client)
                //    {
                //        contexts.Add(item.Key);
                //    }
                //}
                AllDispatchers.Remove(client);

                //foreach (var item in contexts)
                //{
                //    AllDispatchers.Remove(item);
                //}
                client.OnDisconnected?.Invoke();
                OnDisconnectedClientAction?.Invoke(client);
                //GC.Collect();
                //GC.WaitForPendingFinalizers();
                //GC.Collect();
            }
            catch (Exception ex)
            {
                AutoLogger.LogError(ex, "DisposeClient");
            }
        }
        /// <summary>
        /// close Client by ClientInfo
        /// </summary>
        /// <param name="client">client info</param>
        public void CloseClient(ClientInfo client)
        {
            DisposeClient(client, "manualy called CloseClient");
        }
        /// <summary>
        /// close client by session
        /// </summary>
        /// <param name="clientId">client session id</param>
        public void CloseClient(string clientId)
        {
            var client = GetClientByClientId(clientId);
            if (client != null)
                DisposeClient(client, "manualy called CloseClient 2");
        }

        public ClientInfo GetClientByClientId(string clientId)
        {
            Clients.TryGetValue(clientId, out ClientInfo clientInfo);
            return clientInfo;
        }

        /// <summary>
        /// register an inastance of service class for client to call server methods
        /// </summary>
        /// <param name="callInfo"></param>
        /// <param name="client"></param>
        public void CalculateRegisterServiceForClient(MethodCallInfo callInfo, ClientInfo client)
        {
            MethodCallbackInfo callback = new MethodCallbackInfo()
            {
                Guid = callInfo.Guid,
                Data = ServerSerializationHelper.SerializeObject(client.ClientId, this)
            };
            try
            {
                if (!RegisteredServiceTypes.ContainsKey(callInfo.ServiceName))
                    throw new Exception($"Service name {callInfo.ServiceName} not found or not registered!");
                var serviceType = RegisteredServiceTypes[callInfo.ServiceName];
                var serviceTypeAttribute = serviceType.GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault();

                var service = FindClientServiceByType(client, serviceType, serviceTypeAttribute);
                if (service != null && serviceTypeAttribute.InstanceType == InstanceType.MultipeInstance)
                {
                    throw new Exception($"{client.IPAddress} {client.ClientId} this service for this client exist, type: {serviceType.FullName} : serviceName:{callInfo.ServiceName}");
                }
                else if (service == null)
                {
                    RegisterServiceForClient(serviceTypeAttribute, client);
                }

                //جلوگیری از هنگ
                //AsyncActions.Run(objectInstance.OnInitialized);
                //AddedClient?.Invoke(client);
            }
            catch (Exception ex)
            {
                SignalGo.Shared.Log.AutoLogger.LogError(ex, $"{client.IPAddress} {client.ClientId} ServerBase RegisterClassForClient");
                callback.IsException = true;
                callback.Data = ServerSerializationHelper.SerializeObject(ex.ToString(), this);
            }
            SendCallbackData(callback, client);
        }

        public object RegisterServiceForClient(ServiceContractAttribute serviceAttribute, ClientInfo client)
        {
            var serviceType = RegisteredServiceTypes[serviceAttribute.Name];
            var objectInstance = Activator.CreateInstance(serviceType);
            if (serviceAttribute.InstanceType == InstanceType.MultipeInstance)
            {
                if (!Services.ContainsKey(client))
                    Services.TryAdd(client, new ConcurrentList<object>());
                this.Services[client].Add(objectInstance);
            }
            else
                SingleInstanceServices.TryAdd(serviceAttribute.Name, objectInstance);
            return objectInstance;
        }

        public void RegisterMethodsForClient(MethodCallInfo callInfo, ClientInfo client)
        {
            MethodCallbackInfo callback = new MethodCallbackInfo()
            {
                Guid = callInfo.Guid
            };
            try
            {
                //var serviceType = RegisteredServiceTypes[callInfo.ServiceName];
                //var attrib = serviceType.GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault();
                if (!ClientRegistredMethods[client].ContainsKey(callInfo.ServiceName))
                    ClientRegistredMethods[client].TryAdd(callInfo.ServiceName, new ConcurrentList<string>());

                List<string> methodNames = ServerSerializationHelper.Deserialize<List<string>>(callInfo.Data.ToString(), this);
                foreach (var item in methodNames)
                {
                    if (!ClientRegistredMethods[client][callInfo.ServiceName].Contains(item))
                        ClientRegistredMethods[client][callInfo.ServiceName].Add(item);
                }
            }
            catch (Exception ex)
            {
                SignalGo.Shared.Log.AutoLogger.LogError(ex, $"{client.IPAddress} {client.ClientId} ServerBase RegisterMethodsForClient");
                callback.IsException = true;
                callback.Data = ServerSerializationHelper.SerializeObject(ex.ToString(), this);
            }
            SendCallbackData(callback, client);
        }

        public void UnRegisterMethodsForClient(MethodCallInfo callInfo, ClientInfo client)
        {
            MethodCallbackInfo callback = new MethodCallbackInfo()
            {
                Guid = callInfo.Guid
            };
            try
            {
                //var serviceType = RegisteredServiceTypes[callInfo.ServiceName];
                //var attrib = serviceType.GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault();
                if (ClientRegistredMethods[client].ContainsKey(callInfo.ServiceName))
                {
                    List<string> methodNames = ServerSerializationHelper.Deserialize<List<string>>(callInfo.Data.ToString(), this);
                    foreach (var item in methodNames)
                    {
                        if (ClientRegistredMethods[client][callInfo.ServiceName].Contains(item))
                            ClientRegistredMethods[client][callInfo.ServiceName].Remove(item);
                    }
                }
            }
            catch (Exception ex)
            {
                SignalGo.Shared.Log.AutoLogger.LogError(ex, $"{client.IPAddress} {client.ClientId} ServerBase UnRegisterMethodsForClient");
                callback.IsException = true;
                callback.Data = ServerSerializationHelper.SerializeObject(ex.ToString(), this);
            }
            SendCallbackData(callback, client);
        }

        System.Reflection.MethodInfo FindMethodFromBase(Type serviceType, string methodName, Type[] pTypes)
        {
            foreach (var item in serviceType.GetInterfaces())
            {
                var m = item.GetMethod(methodName, pTypes);
                if (m != null)
                    return m;
            }
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
            var parent = serviceType.GetTypeInfo().BaseType;
#else
            var parent = serviceType.BaseType;
#endif
            while (parent != null)
            {
                var m = parent.GetMethod(methodName, pTypes);
                if (m != null)
                    return m;

                foreach (var item in parent.GetInterfaces())
                {
                    m = item.GetMethod(methodName, pTypes);
                    if (m != null)
                        return m;
                }
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                parent = parent.GetTypeInfo().BaseType;
#else
                parent = parent.BaseType;
#endif
            }

            return null;
        }

        internal System.Reflection.MethodInfo GetMethod(MethodCallInfo callInfo, Type serviceType)
        {
            var list = serviceType.GetTypesByAttribute<ServiceContractAttribute>().ToList();
            foreach (var item in list)
            {
                var pTypes = RuntimeTypeHelper.GetMethodTypes(serviceType, callInfo).ToArray();
                var method = item.GetMethod(callInfo.MethodName, pTypes);
                if (method == null)
                    method = FindMethodFromBase(item, callInfo.MethodName, pTypes);
                if (method != null && method.IsPublic)
                    return method;
            }
            return null;
        }

        internal void CallMethod(MethodCallInfo callInfo, ClientInfo client, string json)
        {
            AsyncActions.Run((Action)(() =>
            {

                if (SynchronizationContext.Current == null)
                    SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                if (!AllDispatchers.ContainsKey(SynchronizationContext.Current))
                    AllDispatchers.Add(SynchronizationContext.Current, client);
                MethodCallbackInfo callback = new MethodCallbackInfo()
                {
                    Guid = callInfo.Guid
                };

                object result = null;
                MethodInfo method = null;
                Exception exception = null;
                try
                {
                    ClientConnectedCallingCount++;
                    if (!RegisteredServiceTypes.ContainsKey(callInfo.ServiceName))
                        throw new Exception($"{client.IPAddress} {client.ClientId} Service {callInfo.ServiceName} not found");

                    var serviceType = RegisteredServiceTypes[callInfo.ServiceName];
                    if (serviceType == null)
                        throw new Exception($"{client.IPAddress} {client.ClientId} serviceType {callInfo.ServiceName} not found");
                    var service = FindClientServiceByType(client, serviceType, null);
                    if (service == null)
                        throw new Exception($"{client.IPAddress} {client.ClientId} service {callInfo.ServiceName} not found");

                    method = GetMethod(callInfo, serviceType);// serviceType.GetMethod(callInfo.MethodName, RuntimeTypeHelper.GetMethodTypes(serviceType, callInfo).ToArray());
                    if (method == null)
                    {
                        StringBuilder exceptionResult = new StringBuilder();
                        exceptionResult.AppendLine("<Exception>");
                        exceptionResult.AppendLine($"method {callInfo.MethodName} not found");
                        exceptionResult.AppendLine("<Parameters>");
                        foreach (var item in callInfo.Parameters)
                        {
                            exceptionResult.AppendLine((item.Value ?? "null;") + " type: " + (item.Type ?? "no type"));
                        }
                        exceptionResult.AppendLine("</Parameters>");
                        exceptionResult.AppendLine("<JSON>");
                        exceptionResult.AppendLine(json);
                        exceptionResult.AppendLine("</JSON>");
                        exceptionResult.AppendLine("</Exception>");
                        throw new Exception($"{client.IPAddress} {client.ClientId} " + exceptionResult.ToString());
                    }

                    var serviceMethod = serviceType.GetMethod(method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());

                    var securityAttributes = serviceMethod.GetCustomAttributes(typeof(SecurityContractAttribute), true).ToList();
                    var customDataExchanger = serviceMethod.GetCustomAttributes(typeof(CustomDataExchangerAttribute), true).Cast<CustomDataExchangerAttribute>().Where(x => x.GetExchangerByUserCustomization(client)).ToList();

                    customDataExchanger.AddRange(method.GetCustomAttributes(typeof(CustomDataExchangerAttribute), true).Cast<CustomDataExchangerAttribute>().Where(x => x.GetExchangerByUserCustomization(client)).ToList());
                    securityAttributes.AddRange(method.GetCustomAttributes(typeof(SecurityContractAttribute), true));

                    List<object> parameters = new List<object>();
                    int index = 0;
                    var prms = method.GetParameters();
                    foreach (var item in callInfo.Parameters)
                    {
                        if (item.Value == null)
                            parameters.Add(GetDefault(prms[index].ParameterType));
                        else
                            parameters.Add(ServerSerializationHelper.Deserialize(item.Value, prms[index].ParameterType, this, customDataExchanger: customDataExchanger.ToArray(), client: client));
                        index++;
                    }

                    var clientLimitationAttribute = serviceMethod.GetCustomAttributes(typeof(ClientLimitationAttribute), true).ToList();
                    clientLimitationAttribute.AddRange(method.GetCustomAttributes(typeof(ClientLimitationAttribute), true));

                    foreach (ClientLimitationAttribute attrib in clientLimitationAttribute)
                    {
                        var allowAddresses = attrib.GetAllowAccessIpAddresses();
                        if (allowAddresses != null && allowAddresses.Length > 0)
                        {
                            if (!allowAddresses.Contains(client.IPAddress))
                            {
                                AutoLogger.LogText($"Client IP Have Not Access To Call Method: {client.IPAddress}");
                                return;
                            }
                        }
                        else
                        {
                            var denyAddresses = attrib.GetDenyAccessIpAddresses();
                            if (denyAddresses != null && denyAddresses.Length > 0)
                            {
                                if (denyAddresses.Contains(client.IPAddress))
                                {
                                    AutoLogger.LogText($"Client IP Is Deny Access To Call Method: {client.IPAddress}");
                                    return;
                                }
                            }
                        }
                    }


                    //securityAttributes.AddRange(service.GetType().GetCustomAttributes(typeof(SecurityContractAttribute), true));
                    //securityAttributes.AddRange(serviceType.GetCustomAttributes(typeof(SecurityContractAttribute), true));
                    //var allattrib = serviceMethod.GetCustomAttributes(true);
                    //var t1 = serviceMethod.GetCustomAttributes(typeof(StaticLockAttribute), true).Length;
                    //var t2 = method.GetCustomAttributes(typeof(StaticLockAttribute), true).Length;

                    //when method have static locl attribute calling is going to lock
                    bool isStaticLock = serviceMethod.GetCustomAttributes(typeof(StaticLockAttribute), true).Count() > 0 || method.GetCustomAttributes(typeof(StaticLockAttribute), true).Count() > 0;

                    //if (MethodCallsLogger.IsStart)
                    //    logInfo = MethodCallsLogger.AddCallMethodLog(client.ClientId, client.IPAddress, client.ConnectedDateTime, callInfo.ServiceName, method, callInfo.Parameters);

                    MethodsCallHandler.BeginMethodCallAction?.Invoke(client, callInfo.Guid, callInfo.ServiceName, method, callInfo.Parameters);

                    //check if client have permissions for call method
                    bool canCall = true;
                    foreach (SecurityContractAttribute attrib in securityAttributes)
                    {
                        if (!attrib.CheckPermission(client, service, method, parameters))
                        {
                            callback.IsAccessDenied = true;
                            canCall = false;
                            if (method.ReturnType != typeof(void))
                            {
                                object data = null;
                                data = attrib.GetValueWhenDenyPermission(client, service, method, parameters);
                                callback.Data = data == null ? null : ServerSerializationHelper.SerializeObject(data, this, customDataExchanger: customDataExchanger.ToArray(), client: client);
                            }
                            break;
                        }
                    }

                    if (canCall)
                    {
                        if (method.ReturnType == typeof(void))
                        {
                            if (isStaticLock)
                                lock (StaticLockObject)
                                {
                                    method.Invoke(service, parameters.ToArray());
                                }
                            else
                                method.Invoke(service, parameters.ToArray());
                        }
                        else
                        {
                            if (ErrorHandlingFunction != null)
                            {
                                try
                                {
                                    if (isStaticLock)
                                        lock (StaticLockObject)
                                        {
                                            result = method.Invoke(service, parameters.ToArray());
                                        }
                                    else
                                        result = method.Invoke(service, parameters.ToArray());
                                }
                                catch (Exception ex)
                                {
                                    result = ErrorHandlingFunction(ex);
                                }
                            }
                            else
                            {
                                if (isStaticLock)
                                    lock (StaticLockObject)
                                    {
                                        result = method.Invoke(service, parameters.ToArray());
                                    }
                                else
                                    result = method.Invoke(service, parameters.ToArray());
                            }

                            callback.Data = result == null ? null : ServerSerializationHelper.SerializeObject(result, this, customDataExchanger: customDataExchanger.ToArray(), client: client);
                        }
                    }
                }
                catch (Exception ex)
                {
                    exception = ex;
                    SignalGo.Shared.Log.AutoLogger.LogError(ex, $"{client.IPAddress} {client.ClientId} ServerBase CallMethod: {callInfo.MethodName}");
                    callback.IsException = true;
                    callback.Data = ServerSerializationHelper.SerializeObject(ex.ToString(), this);
                }
                finally
                {
                    ClientConnectedCallingCount--;
                    MethodsCallHandler.EndMethodCallAction?.Invoke(client, callInfo.Guid, callInfo.ServiceName, method, callInfo.Parameters, callback?.Data, exception);
                }
                SendCallbackData(callback, client);
            }));
        }

        internal static bool EquelMethods(MethodInfo method1, MethodInfo method2)
        {
            var find = method1.DeclaringType.GetMethod(method2.Name, method2.GetParameters().Select(p => p.ParameterType).ToArray());
            return find != null;
        }

        public class DefaultGenerator<T>
        {
            public static T GetDefault()
            {
                return default(T);
            }
        }

        internal object GetDefault(Type t)
        {
            try
            {
                var defaultGeneratorType =
      typeof(DefaultGenerator<>).MakeGenericType(t);
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                MethodInfo method = defaultGeneratorType.GetTypeInfo().GetDeclaredMethod("GetDefault");
                return method.Invoke(null, null);
#else
                return defaultGeneratorType.InvokeMember(
                     "GetDefault",
                     BindingFlags.Static |
                     BindingFlags.Public |
                     BindingFlags.InvokeMethod,
                     null, null, new object[0]);
#endif
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        internal T GetDefaultGeneric<T>()
        {
            return default(T);
        }
        //public T GetCallbackByClient<T>(ClientInfo client)
        //{
        //    Type type = typeof(T);
        //    var name = type.GetCustomAttributes<ServiceContractAttribute>(true).FirstOrDefault().Name;
        //    var t = RegisteredCallbacksTypes[name];

        //}

        internal void SetSettings(SecuritySettingsInfo setting, MethodCallInfo callInfo, ClientInfo client, string json)
        {
            try
            {
                MethodCallbackInfo callback = new MethodCallbackInfo()
                {
                    Guid = callInfo.Guid
                };
                if (setting.SecurityMode == SecurityMode.None)
                {
                    SendCallbackData(callback, client);
                    if (ClientsSettings.ContainsKey(client))
                        ClientsSettings.Remove(client);
                }
                else if (setting.SecurityMode == SecurityMode.RSA_AESSecurity)
                {
                    var keys = RSAAESSecurity.GenerateAESKeys();
                    setting.Data.Key = RSASecurity.Encrypt(keys.Key, RSASecurity.StringToKey(setting.Data.RSAEncryptionKey));
                    setting.Data.IV = RSASecurity.Encrypt(keys.IV, RSASecurity.StringToKey(setting.Data.RSAEncryptionKey));
                    callback.Data = ServerSerializationHelper.SerializeObject(setting, this);
                    SendCallbackData(callback, client);
                    setting.Data.Key = keys.Key;
                    setting.Data.IV = keys.IV;
                    ClientsSettings.TryAdd(client, setting);
                }
            }
            catch (Exception ex)
            {
                SignalGo.Shared.Log.AutoLogger.LogError(ex, $"{client.IPAddress} {client.ClientId} ServerBase SetSettings");
                if (!client.TcpClient.Connected)
                    DisposeClient(client, "SetSettings exception");
            }
        }

        internal byte[] DecryptBytes(byte[] bytes, ClientInfo client)
        {
            var setting = ClientsSettings[client];
            return AESSecurity.DecryptBytes(bytes, setting.Data.Key, setting.Data.IV);
        }

        internal byte[] EncryptBytes(byte[] bytes, ClientInfo client)
        {
            var setting = ClientsSettings[client];
            return AESSecurity.EncryptBytes(bytes, setting.Data.Key, setting.Data.IV);
        }

        /// <summary>
        /// ارسال پاسخ تابع صدا زده شده توسط کلاینت
        /// در واقع کلاینت وقتی یک تابع را صدا میزند منتظر میماند تا سرور به آن پاسخ صحیح را برساند و تضمین رسیدن درخواست را به سرور میکند
        /// </summary>
        /// <param name="callback"></param>
        /// <param name="client"></param>
        internal void SendCallbackData(MethodCallbackInfo callback, ClientInfo client)
        {
            try
            {
                ClientConnectedCallingCount++;
                if (client.IsWebSocket)
                {
                    string json = ServerSerializationHelper.SerializeObject(callback, this);
                    if (json.Length > 30000)
                    {
                        var listOfParts = GeneratePartsOfData(json);
                        int i = 1;
                        foreach (var item in listOfParts)
                        {
                            var cb = callback.Clone();
                            cb.PartNumber = i == listOfParts.Count ? (short)-1 : (short)i;
                            cb.Data = item;
                            json = (int)DataType.ResponseCallMethod + "," + (int)CompressMode.None + "/" + ServerSerializationHelper.SerializeObject(cb, this);
                            var result = Encoding.UTF8.GetBytes(json);
                            if (ClientsSettings.ContainsKey(client))
                                result = EncryptBytes(result, client);
                            GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), result, client.IsWebSocket);
                            i++;
                        }
                    }
                    else
                    {
                        json = (int)DataType.ResponseCallMethod + "," + (int)CompressMode.None + "/" + json;
                        var result = Encoding.UTF8.GetBytes(json);
                        if (ClientsSettings.ContainsKey(client))
                            result = EncryptBytes(result, client);
                        GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), result, client.IsWebSocket);
                    }

                }
                else
                {
                    string json = ServerSerializationHelper.SerializeObject(callback, this);
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    if (ClientsSettings.ContainsKey(client))
                        bytes = EncryptBytes(bytes, client);
                    byte[] len = BitConverter.GetBytes(bytes.Length);
                    List<byte> data = new List<byte>
                    {
                        (byte)DataType.ResponseCallMethod,
                        (byte)CompressMode.None
                    };
                    data.AddRange(len);
                    data.AddRange(bytes);
                    if (data.Count > ProviderSetting.MaximumSendDataBlock)
                        throw new Exception($"{client.IPAddress} {client.ClientId} SendCallbackData data length is upper than MaximumSendDataBlock");

                    GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), data.ToArray(), client.IsWebSocket);
                }

            }
            catch (Exception ex)
            {
                SignalGo.Shared.Log.AutoLogger.LogError(ex, $"{client.IPAddress} {client.ClientId} ServerBase SendCallbackData");
                if (!client.TcpClient.Connected)
                    DisposeClient(client, "SendCallbackData exception");
            }
            finally
            {
                ClientConnectedCallingCount--;
            }
        }

        internal List<string> GeneratePartsOfData(string data)
        {
            int partCount = (int)Math.Ceiling((double)data.Length / 30000);
            List<string> partData = new List<string>();
            for (int i = 0; i < partCount; i++)
            {
                if (i != partCount - 1)
                {
                    partData.Add(data.Substring((i * 30000), 30000));
                }
                else
                {
                    partData.Add(data.Substring((i * 30000), data.Length - (i * 30000)));
                }
            }
            return partData;
        }

        /// <summary>
        /// call client methods
        /// </summary>
        /// <param name="client">client</param>
        /// <param name="callInfo">call data</param>
        internal void CallClientMethod(ClientInfo client, MethodCallInfo callInfo)
        {
            AsyncActions.Run(() =>
            {
                MethodsCallHandler.BeginClientMethodCallAction?.Invoke(client, callInfo.Guid, callInfo.ServiceName, callInfo.MethodName, callInfo.Parameters);

                Exception exception = null;
                try
                {
                    ClientConnectedCallingCount++;
                    if (SynchronizationContext.Current == null)
                        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                    AllDispatchers.Add(SynchronizationContext.Current, client);
                    if (client.IsWebSocket)
                    {
                        string json = ServerSerializationHelper.SerializeObject(callInfo, this);
                        ///when length is large we need to send data by parts
                        if (json.Length > 30000)
                        {
                            var listOfParts = GeneratePartsOfData(json);
                            int i = 1;
                            foreach (var item in listOfParts)
                            {
                                var cb = callInfo.Clone();
                                cb.PartNumber = i == listOfParts.Count ? (short)-1 : (short)i;
                                json = (int)DataType.CallMethod + "," + (int)CompressMode.None + "/" + ServerSerializationHelper.SerializeObject(cb, this);
                                var result = Encoding.UTF8.GetBytes(json);
                                if (ClientsSettings.ContainsKey(client))
                                    result = EncryptBytes(result, client);
                                GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), result, client.IsWebSocket);
                                i++;
                            }
                        }
                        else
                        {
                            json = (int)DataType.CallMethod + "," + (int)CompressMode.None + "/" + json;
                            var result = Encoding.UTF8.GetBytes(json);
                            if (ClientsSettings.ContainsKey(client))
                                result = EncryptBytes(result, client);
                            GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), result, client.IsWebSocket);
                        }
                    }
                    else
                    {
                        var json = ServerSerializationHelper.SerializeObject(callInfo, this);
                        List<byte> bytes = new List<byte>
                        {
                            (byte)DataType.CallMethod,
                            (byte)CompressMode.None
                        };
                        var jsonBytes = Encoding.UTF8.GetBytes(json);
                        if (ClientsSettings.ContainsKey(client))
                            jsonBytes = EncryptBytes(jsonBytes, client);
                        byte[] dataLen = BitConverter.GetBytes(jsonBytes.Length);
                        bytes.AddRange(dataLen);
                        bytes.AddRange(jsonBytes);
                        GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), bytes.ToArray(), client.IsWebSocket);
                    }
                }
                catch (Exception ex)
                {
                    exception = ex;
                    AutoLogger.LogError(ex, "CallClientMethod");
                }
                finally
                {
                    ClientConnectedCallingCount--;
                    //MethodsCallHandler.EndClientMethodCallAction?.Invoke(client, callInfo.ServiceName, callInfo.MethodName, callInfo.Parameters, null, exception);
                }
            });
        }

        /// <summary>
        /// send detail of service
        /// </summary>
        /// <param name="client"></param>
        /// <param name="hostUrl">host url that client connected</param>
        List<Type> skippedTypes = new List<Type>();
        internal void SendServiceDetail(ClientInfo client, string hostUrl)
        {
            AsyncActions.Run(() =>
            {
                try
                {

                    var url = new Uri(hostUrl);
                    hostUrl = url.Host + ":" + url.Port;
                    using (var xmlCommentLoader = new XmlCommentLoader())
                    {
                        List<Type> modelTypes = new List<Type>();
                        int id = 1;
                        ProviderDetailsInfo result = new ProviderDetailsInfo() { Id = id };
                        foreach (var service in RegisteredServiceTypes)
                        {

                            id++;
                            var serviceDetail = new ServiceDetailsInfo()
                            {
                                ServiceName = service.Key,
                                FullNameSpace = service.Value.FullName,
                                NameSpace = service.Value.Name,
                                Id = id
                            };
                            result.Services.Add(serviceDetail);
                            List<Type> types = new List<Type>();
                            if (service.Value.GetCustomAttributes<ServiceContractAttribute>(false).Length > 0)
                                types.Add(service.Value);
                            foreach (var item in CSCodeInjection.GetListOfTypes(service.Value))
                            {
                                if (item.GetCustomAttributes<ServiceContractAttribute>(false).Length > 0 && !types.Contains(item))
                                {
                                    types.Add(item);
                                    types.AddRange(CSCodeInjection.GetListOfTypes(service.Value).Where(x => !types.Contains(x)));
                                }
                            }

                            foreach (var serviceType in types)
                            {
                                if (serviceType == typeof(object))
                                    continue;
                                var methods = serviceType.GetMethods().Where(x => !(x.IsSpecialName && (x.Name.StartsWith("set_") || x.Name.StartsWith("get_")))).ToList();
                                if (methods.Count == 0)
                                    continue;
                                var comment = xmlCommentLoader.GetComment(serviceType);
                                id++;
                                var interfaceInfo = new ServiceDetailsInterface()
                                {
                                    NameSpace = serviceType.Name,
                                    FullNameSpace = serviceType.FullName,
                                    Comment = comment?.Summery,
                                    Id = id
                                };
                                serviceDetail.Services.Add(interfaceInfo);
                                List<ServiceDetailsMethod> serviceMethods = new List<ServiceDetailsMethod>();
                                foreach (var method in methods)
                                {
                                    var pType = SerializeHelper.GetTypeCodeOfObject(method.ReturnType);
                                    if (pType == SerializeObjectType.Enum)
                                    {
                                        AddEnumAndNewModels(ref id, method.ReturnType, result, SerializeObjectType.Enum, xmlCommentLoader);
                                    }
                                    var methodComment = comment == null ? null : (from x in comment.Methods where x.Name == method.Name && x.Parameters.Count == method.GetParameters().Length select x).FirstOrDefault();
                                    string exceptions = "";
                                    if (methodComment?.Exceptions != null && methodComment?.Exceptions.Count > 0)
                                    {
                                        foreach (var ex in methodComment.Exceptions)
                                        {
                                            try
                                            {
                                                if (ex.RefrenceType.LastIndexOf('.') != -1)
                                                {
                                                    var baseNameOfEnum = ex.RefrenceType.Substring(0, ex.RefrenceType.LastIndexOf('.'));
                                                    var type = GetEnumType(baseNameOfEnum);
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                                                    if (type != null && type.GetTypeInfo().IsEnum)
#else
                                                    if (type != null && type.IsEnum)
#endif
                                                    {
                                                        var value = Enum.Parse(type, ex.RefrenceType.Substring(ex.RefrenceType.LastIndexOf('.') + 1, ex.RefrenceType.Length - ex.RefrenceType.LastIndexOf('.') - 1));
                                                        int exNumber = (int)value;
                                                        exceptions += ex.RefrenceType + $" ({exNumber}) : " + ex.Comment + Environment.NewLine;
                                                        continue;
                                                    }
                                                }
                                            }
                                            catch
                                            {

                                            }

                                            exceptions += ex.RefrenceType + ":" + ex.Comment + Environment.NewLine;
                                        }
                                    }
                                    id++;
                                    ServiceDetailsMethod info = new ServiceDetailsMethod()
                                    {
                                        MethodName = method.Name,
                                        Parameters = new List<ServiceDetailsParameterInfo>(),
                                        ReturnType = method.ReturnType.GetFriendlyName(),
                                        Comment = methodComment?.Summery,
                                        ReturnComment = methodComment?.Returns,
                                        ExceptionsComment = exceptions,
                                        Id = id
                                    };
                                    RuntimeTypeHelper.GetListOfUsedTypes(method.ReturnType, ref modelTypes);
                                    foreach (var paramInfo in method.GetParameters())
                                    {
                                        pType = SerializeHelper.GetTypeCodeOfObject(paramInfo.ParameterType);
                                        if (pType == SerializeObjectType.Enum)
                                        {
                                            AddEnumAndNewModels(ref id, paramInfo.ParameterType, result, SerializeObjectType.Enum, xmlCommentLoader);
                                        }
                                        string parameterComment = "";
                                        if (methodComment != null)
                                            parameterComment = (from x in methodComment.Parameters where x.Name == paramInfo.Name select x.Comment).FirstOrDefault();
                                        id++;
                                        ServiceDetailsParameterInfo p = new ServiceDetailsParameterInfo()
                                        {
                                            Name = paramInfo.Name,
                                            Type = paramInfo.ParameterType.GetFriendlyName(),
                                            FullTypeName = paramInfo.ParameterType.FullName,
                                            Comment = parameterComment,
                                            Id = id
                                        };
                                        info.Parameters.Add(p);
                                        RuntimeTypeHelper.GetListOfUsedTypes(paramInfo.ParameterType, ref modelTypes);
                                    }
                                    serviceMethods.Add(info);
                                }
                                interfaceInfo.Methods.AddRange(serviceMethods);
                            }
                        }





                        foreach (var service in RegisteredCallbacksTypes)
                        {
                            id++;
                            var serviceDetail = new CallbackServiceDetailsInfo()
                            {
                                ServiceName = service.Key,
                                FullNameSpace = service.Value.FullName,
                                NameSpace = service.Value.Name,
                                Id = id
                            };

                            result.Callbacks.Add(serviceDetail);
                            List<Type> types = new List<Type>();
                            if (service.Value.GetCustomAttributes<ServiceContractAttribute>(false).Length > 0)
                                types.Add(service.Value);
                            foreach (var item in CSCodeInjection.GetListOfTypes(service.Value))
                            {
                                if (item.GetCustomAttributes<ServiceContractAttribute>(false).Length > 0 && !types.Contains(item))
                                {
                                    types.Add(item);
                                    types.AddRange(CSCodeInjection.GetListOfTypes(service.Value).Where(x => !types.Contains(x)));
                                }
                            }

                            var methods = service.Value.GetMethods().Where(x => !(x.IsSpecialName && (x.Name.StartsWith("set_") || x.Name.StartsWith("get_")))).ToList();
                            if (methods.Count == 0)
                                continue;
                            var comment = xmlCommentLoader.GetComment(service.Value);
                            List<ServiceDetailsMethod> serviceMethods = new List<ServiceDetailsMethod>();
                            foreach (var method in methods)
                            {
                                var pType = SerializeHelper.GetTypeCodeOfObject(method.ReturnType);
                                if (pType == SerializeObjectType.Enum)
                                {
                                    AddEnumAndNewModels(ref id, method.ReturnType, result, SerializeObjectType.Enum, xmlCommentLoader);
                                }
                                var methodComment = comment == null ? null : (from x in comment.Methods where x.Name == method.Name && x.Parameters.Count == method.GetParameters().Length select x).FirstOrDefault();
                                string exceptions = "";
                                if (methodComment?.Exceptions != null && methodComment?.Exceptions.Count > 0)
                                {
                                    foreach (var ex in methodComment.Exceptions)
                                    {
                                        try
                                        {
                                            if (ex.RefrenceType.LastIndexOf('.') != -1)
                                            {
                                                var baseNameOfEnum = ex.RefrenceType.Substring(0, ex.RefrenceType.LastIndexOf('.'));
                                                var type = GetEnumType(baseNameOfEnum);
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                                                if (type != null && type.GetTypeInfo().IsEnum)
#else
                                                if (type != null && type.IsEnum)
#endif
                                                {
                                                    var value = Enum.Parse(type, ex.RefrenceType.Substring(ex.RefrenceType.LastIndexOf('.') + 1, ex.RefrenceType.Length - ex.RefrenceType.LastIndexOf('.') - 1));
                                                    int exNumber = (int)value;
                                                    exceptions += ex.RefrenceType + $" ({exNumber}) : " + ex.Comment + Environment.NewLine;
                                                    continue;
                                                }
                                            }
                                        }
                                        catch
                                        {

                                        }

                                        exceptions += ex.RefrenceType + ":" + ex.Comment + Environment.NewLine;
                                    }
                                }
                                id++;
                                ServiceDetailsMethod info = new ServiceDetailsMethod()
                                {
                                    MethodName = method.Name,
                                    Parameters = new List<ServiceDetailsParameterInfo>(),
                                    ReturnType = method.ReturnType.GetFriendlyName(),
                                    Comment = methodComment?.Summery,
                                    ReturnComment = methodComment?.Returns,
                                    ExceptionsComment = exceptions,
                                    Id = id
                                };
                                RuntimeTypeHelper.GetListOfUsedTypes(method.ReturnType, ref modelTypes);
                                foreach (var paramInfo in method.GetParameters())
                                {
                                    pType = SerializeHelper.GetTypeCodeOfObject(paramInfo.ParameterType);
                                    if (pType == SerializeObjectType.Enum)
                                    {
                                        AddEnumAndNewModels(ref id, paramInfo.ParameterType, result, SerializeObjectType.Enum, xmlCommentLoader);
                                    }
                                    string parameterComment = "";
                                    if (methodComment != null)
                                        parameterComment = (from x in methodComment.Parameters where x.Name == paramInfo.Name select x.Comment).FirstOrDefault();
                                    id++;
                                    ServiceDetailsParameterInfo p = new ServiceDetailsParameterInfo()
                                    {
                                        Name = paramInfo.Name,
                                        Type = paramInfo.ParameterType.GetFriendlyName(),
                                        FullTypeName = paramInfo.ParameterType.FullName,
                                        Comment = parameterComment,
                                        Id = id
                                    };
                                    info.Parameters.Add(p);
                                    RuntimeTypeHelper.GetListOfUsedTypes(paramInfo.ParameterType, ref modelTypes);
                                }
                                serviceMethods.Add(info);
                            }
                            serviceDetail.Methods.AddRange(serviceMethods);
                        }






                        foreach (var httpServiceType in RegisteredHttpServiceTypes)
                        {
                            id++;
                            var controller = new HttpControllerDetailsInfo()
                            {
                                Id = id,
                                Url = httpServiceType.Value.GetCustomAttributes<HttpSupportAttribute>(true)[0].Addresses.FirstOrDefault(),
                            };
                            id++;
                            result.WebApiDetailsInfo.Id = id;
                            result.WebApiDetailsInfo.HttpControllers.Add(controller);
                            var methods = httpServiceType.Value.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance).Where(x => !(x.IsSpecialName && (x.Name.StartsWith("set_") || x.Name.StartsWith("get_")))).ToList();
                            if (methods.Count == 0)
                                continue;
                            var comment = xmlCommentLoader.GetComment(httpServiceType.Value);
                            List<ServiceDetailsMethod> serviceMethods = new List<ServiceDetailsMethod>();
                            foreach (var method in methods)
                            {
                                var pType = SerializeHelper.GetTypeCodeOfObject(method.ReturnType);
                                if (pType == SerializeObjectType.Enum)
                                {
                                    AddEnumAndNewModels(ref id, method.ReturnType, result, SerializeObjectType.Enum, xmlCommentLoader);
                                }
                                var methodComment = comment == null ? null : (from x in comment.Methods where x.Name == method.Name && x.Parameters.Count == method.GetParameters().Length select x).FirstOrDefault();
                                string exceptions = "";
                                if (methodComment?.Exceptions != null && methodComment?.Exceptions.Count > 0)
                                {
                                    foreach (var ex in methodComment.Exceptions)
                                    {
                                        try
                                        {
                                            if (ex.RefrenceType.LastIndexOf('.') != -1)
                                            {
                                                var baseNameOfEnum = ex.RefrenceType.Substring(0, ex.RefrenceType.LastIndexOf('.'));
                                                var type = GetEnumType(baseNameOfEnum);
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                                                if (type != null && type.GetTypeInfo().IsEnum)
#else
                                                if (type != null && type.IsEnum)
#endif
                                                {
                                                    var value = Enum.Parse(type, ex.RefrenceType.Substring(ex.RefrenceType.LastIndexOf('.') + 1, ex.RefrenceType.Length - ex.RefrenceType.LastIndexOf('.') - 1));
                                                    int exNumber = (int)value;
                                                    exceptions += ex.RefrenceType + $" ({exNumber}) : " + ex.Comment + Environment.NewLine;
                                                    continue;
                                                }
                                            }
                                        }
                                        catch
                                        {

                                        }

                                        exceptions += ex.RefrenceType + ":" + ex.Comment + Environment.NewLine;
                                    }
                                }
                                id++;
                                ServiceDetailsMethod info = new ServiceDetailsMethod()
                                {
                                    Id = id,
                                    MethodName = method.Name,
                                    Parameters = new List<ServiceDetailsParameterInfo>(),
                                    ReturnType = method.ReturnType.GetFriendlyName(),
                                    Comment = methodComment?.Summery,
                                    ReturnComment = methodComment?.Returns,
                                    ExceptionsComment = exceptions,
                                    TestExample = hostUrl + "/" + controller.Url + "/" + method.Name
                                };

                                string testExampleParams = "";
                                foreach (var paramInfo in method.GetParameters())
                                {
                                    pType = SerializeHelper.GetTypeCodeOfObject(paramInfo.ParameterType);
                                    if (pType == SerializeObjectType.Enum)
                                    {
                                        AddEnumAndNewModels(ref id, paramInfo.ParameterType, result, SerializeObjectType.Enum, xmlCommentLoader);
                                    }
                                    string parameterComment = "";
                                    if (methodComment != null)
                                        parameterComment = (from x in methodComment.Parameters where x.Name == paramInfo.Name select x.Comment).FirstOrDefault();
                                    id++;
                                    ServiceDetailsParameterInfo p = new ServiceDetailsParameterInfo()
                                    {
                                        Id = id,
                                        Name = paramInfo.Name,
                                        Type = paramInfo.ParameterType.Name,
                                        FullTypeName = paramInfo.ParameterType.FullName,
                                        Comment = parameterComment
                                    };
                                    info.Parameters.Add(p);
                                    if (string.IsNullOrEmpty(testExampleParams))
                                        testExampleParams += "?";
                                    else
                                        testExampleParams += "&";
                                    testExampleParams += paramInfo.Name + "=" + GetDefault(paramInfo.ParameterType) ?? "null";
                                    RuntimeTypeHelper.GetListOfUsedTypes(paramInfo.ParameterType, ref modelTypes);
                                }
                                info.TestExample += testExampleParams;
                                serviceMethods.Add(info);
                            }
                            controller.Methods = serviceMethods;
                        }

                        foreach (var type in modelTypes)
                        {
                            try
                            {
                                var pType = SerializeHelper.GetTypeCodeOfObject(type);
                                AddEnumAndNewModels(ref id, type, result, pType, xmlCommentLoader);
                                //                                var mode = SerializeHelper.GetTypeCodeOfObject(type);
                                //                                if (mode == SerializeObjectType.Object)
                                //                                {
                                //                                    if (type.Name.Contains("`") || type == typeof(CustomAttributeTypedArgument) || type == typeof(CustomAttributeNamedArgument) ||
                                //#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                                //                                        type.GetTypeInfo().BaseType == typeof(Attribute))
                                //#else
                                //                                    type.BaseType == typeof(Attribute))
                                //#endif
                                //                                        continue;

                                //                                    var instance = Activator.CreateInstance(type);
                                //                                    string jsonResult = JsonConvert.SerializeObject(instance, Formatting.Indented, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Include });
                                //                                    var refactorResult = (JObject)JsonConvert.DeserializeObject(jsonResult);
                                //                                    foreach (var item in refactorResult.Properties())
                                //                                    {
                                //                                        var find = type.GetProperties().FirstOrDefault(x => x.Name == item.Name);
                                //                                        refactorResult[item.Name] = find.PropertyType.FullName;
                                //                                    }
                                //                                    jsonResult = JsonConvert.SerializeObject(refactorResult, Formatting.Indented, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Include });

                                //                                    if (jsonResult == "{}" || jsonResult == "[]")
                                //                                        continue;
                                //                                    var comment = xmlCommentLoader.GetComment(type);
                                //                                    id++;
                                //                                    result.ProjectDomainDetailsInfo.Id = id;
                                //                                    id++;
                                //                                    result.ProjectDomainDetailsInfo.Models.Add(new ModelDetailsInfo()
                                //                                    {
                                //                                        Id = id,
                                //                                        Comment = comment?.Summery,
                                //                                        Name = type.Name,
                                //                                        FullNameSpace = type.FullName,
                                //                                        ObjectType = mode,
                                //                                        JsonTemplate = jsonResult
                                //                                    });
                                //                                    foreach (var property in type.GetProperties())
                                //                                    {
                                //                                        var pType = SerializeHelper.GetTypeCodeOfObject(property.PropertyType);
                                //                                        if (pType == SerializeObjectType.Enum)
                                //                                        {
                                //                                            AddEnumAndNewModels(ref id, property.PropertyType, result, SerializeObjectType.Enum, xmlCommentLoader);
                                //                                        }
                                //                                    }
                                //                                }
                            }
                            catch (Exception ex)
                            {
                                AutoLogger.LogError(ex, "add model type error: " + ex.ToString());
                            }
                        }

                        var json = ServerSerializationHelper.SerializeObject(result, this);
                        List<byte> bytes = new List<byte>
                        {
                            (byte)DataType.GetServiceDetails,
                            (byte)CompressMode.None
                        };
                        var jsonBytes = Encoding.UTF8.GetBytes(json);
                        if (ClientsSettings.ContainsKey(client))
                            jsonBytes = EncryptBytes(jsonBytes, client);
                        byte[] dataLen = BitConverter.GetBytes(jsonBytes.Length);
                        bytes.AddRange(dataLen);
                        bytes.AddRange(jsonBytes);
                        GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), bytes.ToArray(), client.IsWebSocket);
                    }
                }
                catch (Exception ex)
                {
                    var json = ServerSerializationHelper.SerializeObject(new Exception(ex.ToString()), this);
                    List<byte> bytes = new List<byte>
                    {
                        (byte)DataType.GetServiceDetails,
                        (byte)CompressMode.None
                    };
                    var jsonBytes = Encoding.UTF8.GetBytes(json);
                    if (ClientsSettings.ContainsKey(client))
                        jsonBytes = EncryptBytes(jsonBytes, client);
                    byte[] dataLen = BitConverter.GetBytes(jsonBytes.Length);
                    bytes.AddRange(dataLen);
                    bytes.AddRange(jsonBytes);
                    GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), bytes.ToArray(), client.IsWebSocket);

                    SignalGo.Shared.Log.AutoLogger.LogError(ex, $"{client.IPAddress} {client.ClientId} ServerBase CallMethod");
                }
                finally
                {
                    skippedTypes.Clear();
                }
            });

            void AddEnumAndNewModels(ref int id, Type type, ProviderDetailsInfo result, SerializeObjectType objType, XmlCommentLoader xmlCommentLoader)
            {
                if (result.ProjectDomainDetailsInfo.Models.Any(x => x.FullNameSpace == type.FullName) || skippedTypes.Contains(type))
                    return;
                id++;
                result.ProjectDomainDetailsInfo.Id = id;
                id++;
                if (objType == SerializeObjectType.Enum)
                {
                    List<string> items = new List<string>();
                    foreach (Enum obj in Enum.GetValues(type))
                    {
                        int x = Convert.ToInt32(obj); // x is the integer value of enum
                        items.Add(obj.ToString() + " = " + x);
                    }

                    result.ProjectDomainDetailsInfo.Models.Add(new ModelDetailsInfo()
                    {
                        Id = id,
                        Name = type.Name,
                        FullNameSpace = type.FullName,
                        ObjectType = objType,
                        JsonTemplate = JsonConvert.SerializeObject(items, Formatting.Indented, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Include })
                    });
                }
                else
                {
                    try
                    {
                        if (type.Name.Contains("`") || type == typeof(CustomAttributeTypedArgument) || type == typeof(CustomAttributeNamedArgument) ||
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                                    type.GetTypeInfo().BaseType == typeof(Attribute) || type.GetTypeInfo().BaseType == null)
#else
                                    type.BaseType == typeof(Attribute) || type.BaseType == null)
#endif
                        {
                            skippedTypes.Add(type);
                            return;
                        }

                        var instance = Activator.CreateInstance(type);
                        string jsonResult = JsonConvert.SerializeObject(instance, Formatting.Indented, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Include });
                        var refactorResult = (JObject)JsonConvert.DeserializeObject(jsonResult);
                        foreach (var item in refactorResult.Properties())
                        {
                            var find = type.GetProperties().FirstOrDefault(x => x.Name == item.Name);
                            refactorResult[item.Name] = find.PropertyType.GetFriendlyName();
                        }
                        jsonResult = JsonConvert.SerializeObject(refactorResult, Formatting.Indented, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Include });

                        if (jsonResult == "{}" || jsonResult == "[]")
                        {
                            skippedTypes.Add(type);
                            return;
                        }
                        var comment = xmlCommentLoader.GetComment(type);
                        id++;
                        result.ProjectDomainDetailsInfo.Id = id;
                        id++;
                        result.ProjectDomainDetailsInfo.Models.Add(new ModelDetailsInfo()
                        {
                            Id = id,
                            Comment = comment?.Summery,
                            Name = type.Name,
                            FullNameSpace = type.FullName,
                            ObjectType = objType,
                            JsonTemplate = jsonResult
                        });
                    }
                    catch (Exception ex)
                    {
                        skippedTypes.Add(type);
                    }
                }

                foreach (var item in type.GetListOfGenericArguments())
                {
                    var pType = SerializeHelper.GetTypeCodeOfObject(item);
                    AddEnumAndNewModels(ref id, item, result, pType, xmlCommentLoader);
                }

                foreach (var item in type.GetListOfInterfaces())
                {
                    var pType = SerializeHelper.GetTypeCodeOfObject(item);
                    AddEnumAndNewModels(ref id, item, result, pType, xmlCommentLoader);
                }

                foreach (var item in type.GetListOfNestedTypes())
                {
                    var pType = SerializeHelper.GetTypeCodeOfObject(item);
                    AddEnumAndNewModels(ref id, item, result, pType, xmlCommentLoader);
                }

                foreach (var item in type.GetListOfBaseTypes())
                {
                    var pType = SerializeHelper.GetTypeCodeOfObject(item);
                    AddEnumAndNewModels(ref id, item, result, pType, xmlCommentLoader);
                }
                foreach (var property in type.GetProperties())
                {
                    var pType = SerializeHelper.GetTypeCodeOfObject(property.PropertyType);
                    AddEnumAndNewModels(ref id, property.PropertyType, result, pType, xmlCommentLoader);

                }
            }
        }





        internal static Type GetEnumType(string enumName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(enumName);
                if (type == null)
                    continue;
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                if (type.GetTypeInfo().IsEnum)
                    return type;
#else
                if (type.IsEnum)
                    return type;
#endif
            }
            return null;
        }

        internal void SendMethodParameterDetail(ClientInfo client, MethodParameterDetails detail)
        {
            AsyncActions.Run(() =>
            {
                try
                {
                    if (!RegisteredServiceTypes.ContainsKey(detail.ServiceName))
                        throw new Exception($"{client.IPAddress} {client.ClientId} Service {detail.ServiceName} not found");
                    var serviceType = RegisteredServiceTypes[detail.ServiceName];
                    if (serviceType == null)
                        throw new Exception($"{client.IPAddress} {client.ClientId} serviceType {detail.ServiceName} not found");

                    string json = "method or parameter not found";
                    foreach (var method in serviceType.GetMethods())
                    {
                        if (method.IsSpecialName && (method.Name.StartsWith("set_") || method.Name.StartsWith("get_")))
                            continue;
                        if (method.Name == detail.MethodName && detail.ParametersCount == method.GetParameters().Length)
                        {
                            var parameterType = method.GetParameters()[detail.ParameterIndex].ParameterType;
                            if (detail.IsFull)
                                json = TypeToJsonString(parameterType);
                            else
                                json = SimpleTypeToJsonString(parameterType);
                            break;
                        }
                    }
                    List<byte> bytes = new List<byte>
                    {
                        (byte)DataType.GetMethodParameterDetails,
                        (byte)CompressMode.None
                    };
                    var jsonBytes = Encoding.UTF8.GetBytes(json);
                    if (ClientsSettings.ContainsKey(client))
                        jsonBytes = EncryptBytes(jsonBytes, client);
                    byte[] dataLen = BitConverter.GetBytes(jsonBytes.Length);
                    bytes.AddRange(dataLen);
                    bytes.AddRange(jsonBytes);
                    GoStreamWriter.WriteToStream(client.TcpClient.GetStream(), bytes.ToArray(), client.IsWebSocket);
                }
                catch (Exception ex)
                {
                    SignalGo.Shared.Log.AutoLogger.LogError(ex, $"{client.IPAddress} {client.ClientId} ServerBase CallMethod");
                }
            });
        }

        string SimpleTypeToJsonString(Type type)
        {
            object instance = null;

            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {

            }
            if (instance == null)
                return "cannot create instance of this type!";
            return ServerSerializationHelper.SerializeObject(instance, null, NullValueHandling.Include);
        }

        string TypeToJsonString(Type type)
        {
            List<Type> createdInstance = new List<Type>();
            return ServerSerializationHelper.SerializeObject(CreateInstances(type, createdInstance), null, NullValueHandling.Include);
            object CreateInstances(Type newType, List<Type> items)
            {
                if (items.Contains(newType))
                    return GetDefault(newType);
                items.Add(newType);

                object result = null;
                var typeCode = SerializeHelper.GetTypeCodeOfObject(newType);
                if (typeCode == SerializeObjectType.Object)
                {
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                    if (!newType.GetTypeInfo().IsInterface)
#else
                    if (!newType.IsInterface)
#endif
                    {
                        try
                        {
                            result = Activator.CreateInstance(newType);
                            foreach (var item in newType.GetProperties())
                            {
                                item.SetValue(result, CreateInstances(item.PropertyType, items), null);
                            }
                        }
                        catch (Exception)
                        {


                        }
                    }
                    else if (newType.GetGenericTypeDefinition() == typeof(ICollection<>)
                        || newType.GetGenericTypeDefinition() == typeof(IList<>))
                    {
                        try
                        {
                            var gType = newType.GetListOfGenericArguments().FirstOrDefault();
                            var listType = typeof(List<>).MakeGenericType(gType);
                            result = Activator.CreateInstance(listType);
                        }
                        catch (Exception ex)
                        {

                        }
                    }
                }
                else
                {
                    result = GetDefault(newType);
                }
#if (NETSTANDARD1_6 || NETCOREAPP1_1)
                if (newType.GetTypeInfo().IsGenericType && result != null)
#else
                if (newType.IsGenericType && result != null)
#endif
                {
                    if (newType.GetGenericTypeDefinition() == typeof(List<>) || newType.GetGenericTypeDefinition() == typeof(ICollection<>)
                        || newType.GetGenericTypeDefinition() == typeof(IList<>))
                    {
                        var gType = newType.GetListOfGenericArguments().FirstOrDefault();
                        if (gType != null)
                        {
                            try
                            {
                                var gResult = Activator.CreateInstance(gType);
                                foreach (var item in gType.GetProperties())
                                {
                                    item.SetValue(gResult, CreateInstances(item.PropertyType, items), null);
                                }
                                result.GetType().GetMethod("Add").Invoke(result, new object[] { gResult });
                                //col.Add(gResult);
                            }
                            catch (Exception ex)
                            {


                            }
                        }
                    }
                }
                return result;
            }


        }


        /// <summary>
        /// error handling for return type methods (not void)
        /// </summary>
        public Func<Exception, object> ErrorHandlingFunction { get; set; }
        /// <summary>
        /// error handling for http methods
        /// </summary>
        public Func<Exception, object> HTTPErrorHandlingFunction { get; set; }
        /// <summary>
        /// static lock for calling methods when method using StaticLock Attribute
        /// </summary>
        public object StaticLockObject { get; set; } = new object();

        public abstract StreamInfo RegisterFileToDownload(NetworkStream stream, CompressMode compressMode, ClientInfo client, bool isWebSocket);
        public abstract void RegisterFileToUpload(NetworkStream stream, CompressMode compressMode, ClientInfo client, bool isWebSocket);
        public abstract void WriteStreamToClient(StreamInfo streamInfo, NetworkStream toWrite, bool isWebSocket);

        public abstract void UploadStreamToClient(NetworkStream stream, ClientInfo client);
        public abstract void DownloadStreamFromClient(NetworkStream stream, ClientInfo client);


        private volatile bool _IsFinishingServer = false;
        /// <summary>
        /// is server going to finish
        /// </summary>
        public bool IsFinishingServer
        {
            get
            {
                return _IsFinishingServer;
            }
            set
            {
                _IsFinishingServer = value;
            }
        }

        private Action CallAfterFinishAction { get; set; }

        public void StopMethodsCallsForFinishServer(Action _CallAfterFinishAction)
        {
            CallAfterFinishAction = _CallAfterFinishAction;
            IsFinishingServer = true;
            if (CallingCount == 0)
                CallAfterFinishAction?.Invoke();
        }

        /// <summary>
        /// when server is disposed
        /// </summary>
        public bool IsDisposed { get; set; }
        /// <summary>
        /// dispose service
        /// </summary>
        public void Dispose()
        {
            IsDisposed = true;
            Stop();
        }

        public void Stop()
        {
            foreach (var item in Clients.ToList())
            {
                DisposeClient(item.Value, "server stopped");
            }
            server.Stop();
            IsStarted = false;
            OnServerDisconnectedAction?.Invoke();
        }
    }
}
