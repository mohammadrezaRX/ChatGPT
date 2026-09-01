using System;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace MultiplayerCampaign
{
    internal static class MpcNetworkReconnectController
    {
        private static readonly object Sync = new object();
        private static int Generation;
        private static CancellationTokenSource ActiveCts;

        public static void Start(MultiplayerNetworkClient client, string ip)
        {
            if (client == null)
                return;

            if (string.IsNullOrWhiteSpace(ip))
            {
                SetStatus(client, "CONNECTION FAILED: INVALID ADDRESS");
                return;
            }

            CancellationTokenSource cts;
            int generation;

            lock (Sync)
            {
                CancelAndClose(client);
                generation = ++Generation;

                ResetNetworkState();

                cts = new CancellationTokenSource();
                ActiveCts = cts;

                SetField(client, "_cts", cts);
                SetField(client, "_connectionRunning", true);
                SetField(client, "_worldReady", false);
                SetField(client, "_worldLoaded", false);
                SetPropertyBackingField(client, "IsConnected", false);
            }

            SetStatus(client, "CONNECTING");
            _ = RunConnectAsync(client, ip.Trim(), cts, generation);
        }

        public static void Disconnect(MultiplayerNetworkClient client)
        {
            if (client == null)
                return;

            lock (Sync)
            {
                ++Generation;
                ActiveCts = null;

                CancelAndClose(client);
                SetField(client, "_connectionRunning", false);
                SetField(client, "_worldReady", false);
                SetField(client, "_worldLoaded", false);
                SetPropertyBackingField(client, "IsConnected", false);
            }

            try { MultiplayerConnectionStatus.Set(MultiplayerConnectionState.Disconnected); } catch { }
        }

        private static async Task RunConnectAsync(
            MultiplayerNetworkClient client,
            string ip,
            CancellationTokenSource cts,
            int generation)
        {
            TcpClient socket = null;

            try
            {
                socket = new TcpClient
                {
                    NoDelay = true
                };

                Task connectTask = socket.ConnectAsync(ip, 25565);
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                Task completed = await Task.WhenAny(connectTask, timeoutTask);

                if (completed != connectTask)
                {
                    socket.Close();
                    if (!cts.IsCancellationRequested)
                        throw new TimeoutException("Connection attempt timed out after 10 seconds.");
                    return;
                }

                await connectTask;

                if (!IsCurrent(generation, cts) || cts.IsCancellationRequested)
                {
                    socket.Close();
                    return;
                }

                NetworkStream stream = socket.GetStream();
                SetField(client, "_tcpClient", socket);
                SetField(client, "_stream", stream);
                SetPropertyBackingField(client, "IsConnected", true);
                TrySetConnectionState(MultiplayerConnectionState.Connected);

                WriteConsole("[*] TCP connection established.");

                InvokePrivate(client, "SendHello");
                SetStatus(client, "CONNECTED - RECEIVING MCC");

                Task receiveTask = InvokePrivateTask(
                    client,
                    "ReceiveLoopAsync",
                    cts.Token);

                if (receiveTask != null)
                    await receiveTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (IsCurrent(generation, cts))
                {
                    SetPropertyBackingField(client, "IsConnected", false);
                    SetField(client, "_connectionRunning", false);
                    TrySetConnectionState(MultiplayerConnectionState.Disconnected);
                    SetStatus(client, "CONNECTION FAILED: " + ex.Message);
                    WriteConsole("[!] TCP connection error: " + ex.Message);
                }
            }
            finally
            {
                lock (Sync)
                {
                    if (IsCurrent(generation, cts))
                    {
                        SetPropertyBackingField(client, "IsConnected", false);
                        SetField(client, "_connectionRunning", false);

                        TcpClient current = GetField<TcpClient>(client, "_tcpClient");
                        if (ReferenceEquals(current, socket))
                        {
                            SetField(client, "_stream", null);
                            SetField(client, "_tcpClient", null);
                            SetField(client, "_cts", null);
                            ActiveCts = null;
                        }

                        TrySetConnectionState(MultiplayerConnectionState.Disconnected);
                    }
                }

                try { socket?.Close(); } catch { }
            }
        }

        private static bool IsCurrent(int generation, CancellationTokenSource cts)
        {
            lock (Sync)
            {
                return generation == Generation &&
                       ReferenceEquals(ActiveCts, cts);
            }
        }

        private static void CancelAndClose(MultiplayerNetworkClient client)
        {
            try
            {
                CancellationTokenSource old = GetField<CancellationTokenSource>(client, "_cts");
                old?.Cancel();
                old?.Dispose();
            }
            catch { }

            try { GetField<NetworkStream>(client, "_stream")?.Close(); } catch { }
            try { GetField<TcpClient>(client, "_tcpClient")?.Close(); } catch { }

            SetField(client, "_stream", null);
            SetField(client, "_tcpClient", null);
            SetField(client, "_cts", null);
        }

        private static void ResetNetworkState()
        {
            try { HandshakeState.Reset(); } catch { }
            try { NetworkIdentityService.Reset(); } catch { }
            try { MultiplayerConnectionStatus.Set(MultiplayerConnectionState.Connecting); } catch { }
            try { WorldTransferService.Reset(); } catch { }
            try { MultiplayerWorldTransfer.Clear(); } catch { }
        }

        private static void SetStatus(MultiplayerNetworkClient client, string message)
        {
            try { client.SetStatusDirect(message); } catch { }
        }

        private static void TrySetConnectionState(MultiplayerConnectionState state)
        {
            try { MultiplayerConnectionStatus.Set(state); } catch { }
        }

        private static void WriteConsole(string message)
        {
            try { HostConsole.WriteLine(message); } catch { }
        }

        private static T GetField<T>(object instance, string name)
        {
            FieldInfo field = AccessTools.Field(instance.GetType(), name);
            if (field == null)
                return default(T);

            object value = field.GetValue(instance);
            return value is T typed ? typed : default(T);
        }

        private static void SetField(object instance, string name, object value)
        {
            try
            {
                FieldInfo field = AccessTools.Field(instance.GetType(), name);
                field?.SetValue(instance, value);
            }
            catch { }
        }

        private static void SetPropertyBackingField(object instance, string propertyName, bool value)
        {
            try
            {
                FieldInfo field = AccessTools.Field(
                    instance.GetType(),
                    "<" + propertyName + ">k__BackingField");

                field?.SetValue(instance, value);
            }
            catch { }
        }

        private static void InvokePrivate(object instance, string methodName)
        {
            try
            {
                MethodInfo method = AccessTools.Method(instance.GetType(), methodName);
                method?.Invoke(instance, null);
            }
            catch { }
        }

        private static Task InvokePrivateTask(
            object instance,
            string methodName,
            CancellationToken token)
        {
            try
            {
                MethodInfo method = AccessTools.Method(
                    instance.GetType(),
                    methodName,
                    new[] { typeof(CancellationToken) });

                object result = method?.Invoke(
                    instance,
                    new object[] { token });

                return result as Task;
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch(typeof(MultiplayerNetworkClient), "Connect")]
    internal static class MpcNetworkReconnectConnectPatch
    {
        private static bool Prefix(
            MultiplayerNetworkClient __instance,
            string ip)
        {
            MpcNetworkReconnectController.Start(
                __instance,
                ip);

            return false;
        }
    }

    [HarmonyPatch(typeof(MultiplayerNetworkClient), "Disconnect")]
    internal static class MpcNetworkReconnectDisconnectPatch
    {
        private static bool Prefix(
            MultiplayerNetworkClient __instance)
        {
            MpcNetworkReconnectController.Disconnect(
                __instance);

            return false;
        }
    }
}
