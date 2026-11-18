using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VpnWatch
{
    internal sealed class VpnProfile
    {
        public readonly string Name, Book;
        public readonly Guid Id;
        public readonly bool AllUsers;

        public VpnProfile(string name, string book, Guid id, bool allUsers)
        {
            Name = name; Book = book; Id = id; AllUsers = allUsers;
        }

        public override string ToString() { return AllUsers ? Name + " (общий)" : Name; }
    }

    internal enum LinkState { Down, Connecting, Up, Disconnecting }

    internal sealed class RasFailure : Exception
    {
        public readonly uint Code;
        public const uint NoSavedPassword = 0xFFFFFFFF;
        public RasFailure(uint code) : base(Ras.ErrorText(code)) { Code = code; }
    }

    internal static unsafe class Ras
    {
        private const uint BufferTooSmall = 603, InvalidHandle = 6;
        private const uint Connected = 0x2000, Disconnected = 0x2001;
        private const uint EntryTypeVpn = 2;
        private const int MaxBuffer = 8 * 1024 * 1024;
        private static int nextCallback;
        private static readonly ConcurrentDictionary<UIntPtr, DialAttempt> Attempts = new ConcurrentDictionary<UIntPtr, DialAttempt>();
        private static readonly DialCallback Callback = OnDial;

        // ras.h and rasdlg.h use four-byte packing, including on x64.
        // Fixed character buffers are UTF-16, independent of .NET string marshaling.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct EntryName
        {
            public uint Size;
            public fixed char Name[257];
            public uint Flags;
            public fixed char Book[261];
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct EntryProperties
        {
            public uint Size, Options, CountryId, CountryCode;
            public fixed char Area[11], Phone[129];
            public uint AlternateOffset;
            public fixed byte IpAddresses[20];
            public uint FrameSize, NetProtocols, FramingProtocol;
            public fixed char Script[260], AutodialDll[260], AutodialFunc[260];
            public fixed char DeviceType[17], DeviceName[129], X25PadType[33], X25Address[201], X25Facilities[201], X25UserData[201];
            public uint Channels, Reserved1, Reserved2, SubEntries, DialMode, DialExtraPercent, DialExtraSeconds;
            public uint HangUpExtraPercent, HangUpExtraSeconds, IdleDisconnectSeconds, Type, EncryptionType, CustomAuthKey;
            public Guid Id;
            public fixed char CustomDialDll[260];
            public uint VpnStrategy, Options2, Options3;
            public fixed char DnsSuffix[256];
            public uint TcpWindowSize;
            public fixed char PrerequisiteBook[260], PrerequisiteEntry[257];
            public uint RedialCount, RedialPause;
            public fixed byte DnsV6Addresses[32];
            public uint IPv4Metric, IPv6Metric;
            public fixed byte IPv6Address[16];
            public uint IPv6PrefixLength, NetworkOutageTime;
            public fixed char Idi[257], Idr[257];
            public uint IsImsConfig, IdiType, IdrType, DisableIkev2Fragmentation;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct Connection
        {
            public uint Size;
            public IntPtr Handle;
            public fixed char Name[257], DeviceType[17], DeviceName[129], Book[260];
            public uint SubEntry;
            public Guid Id;
            public uint Flags, LuidLow, LuidHigh;
            public Guid Correlation;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TunnelEndpoint
        {
            public uint Type;
            public fixed byte Address[16];
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ConnectionStatus
        {
            public uint Size, State, Error;
            public fixed char DeviceType[17], DeviceName[129], Phone[129];
            public TunnelEndpoint Local, Remote;
            public uint SubState;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct DialParameters
        {
            public uint Size;
            public fixed char Name[257], Phone[129], CallbackNumber[129], User[257], PasswordHandle[257], Domain[16];
            public uint SubEntry;
            public UIntPtr CallbackId;
            public uint InterfaceIndex;
            public IntPtr EncryptedPassword;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct DialDialog
        {
            public uint Size;
            public IntPtr Owner;
            public uint Flags;
            public int X, Y;
            public uint SubEntry, Error;
            public UIntPtr Reserved, Reserved2;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate uint DialCallback(UIntPtr id, uint subEntry, IntPtr connection, uint message, uint state, uint error, uint extended);

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint RasEnumEntriesW(string reserved, string book, IntPtr entries, ref uint bytes, out uint count);
        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint RasGetEntryPropertiesW(string book, string entry, IntPtr properties, ref uint bytes, IntPtr device, IntPtr deviceBytes);
        [DllImport("rasapi32.dll", ExactSpelling = true)]
        private static extern uint RasEnumConnectionsW(IntPtr connections, ref uint bytes, out uint count);
        [DllImport("rasapi32.dll", ExactSpelling = true)]
        private static extern uint RasGetConnectStatusW(IntPtr connection, IntPtr status);
        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint RasGetEntryDialParamsW(string book, IntPtr parameters, out int savedPassword);
        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint RasDialW(IntPtr extensions, string book, IntPtr parameters, uint notifierType, DialCallback notifier, out IntPtr connection);
        [DllImport("rasapi32.dll", ExactSpelling = true)]
        private static extern uint RasHangUpW(IntPtr connection);
        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint RasGetErrorStringW(uint error, StringBuilder text, uint size);
        [DllImport("rasdlg.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RasDialDlgW(string book, string entry, string phone, IntPtr dialog);

        private delegate uint Enumerator(IntPtr buffer, ref uint bytes, out uint count);

        private static void Enumerate(int itemSize, Enumerator enumerate, Action<IntPtr> visit)
        {
            uint bytes = (uint)itemSize;
            for (int retry = 0; retry < 4; retry++)
            {
                if (bytes < itemSize || bytes > MaxBuffer) throw new RasFailure(BufferTooSmall);
                uint capacity = bytes;
                IntPtr buffer = Marshal.AllocHGlobal((int)capacity);
                try
                {
                    Marshal.WriteInt32(buffer, itemSize);
                    uint count;
                    uint result = enumerate(buffer, ref bytes, out count);
                    if (result == BufferTooSmall) continue;
                    Check(result);
                    if (count > capacity / itemSize) throw new RasFailure(BufferTooSmall);
                    for (int i = 0; i < count; i++) visit(IntPtr.Add(buffer, i * itemSize));
                    return;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new RasFailure(BufferTooSmall);
        }

        private static EntryProperties ReadEntry(string book, string name)
        {
            uint bytes = (uint)sizeof(EntryProperties);
            for (int retry = 0; retry < 3; retry++)
            {
                if (bytes < sizeof(EntryProperties) || bytes > MaxBuffer) throw new RasFailure(BufferTooSmall);
                IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    Marshal.WriteInt32(buffer, sizeof(EntryProperties));
                    uint result = RasGetEntryPropertiesW(book, name, buffer, ref bytes, IntPtr.Zero, IntPtr.Zero);
                    if (result == BufferTooSmall) continue;
                    Check(result);
                    return *(EntryProperties*)buffer;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new RasFailure(BufferTooSmall);
        }

        public static List<VpnProfile> Profiles()
        {
            var result = new List<VpnProfile>();
            Enumerate(sizeof(EntryName),
                delegate(IntPtr buffer, ref uint bytes, out uint count) { return RasEnumEntriesW(null, null, buffer, ref bytes, out count); },
                delegate(IntPtr address)
                {
                    EntryName* entry = (EntryName*)address;
                    string name = new string(entry->Name), book = new string(entry->Book);
                    if (book.Length == 0) book = null;
                    EntryProperties properties;
                    try { properties = ReadEntry(book, name); }
                    catch (RasFailure ex) { if (ex.Code == 623) return; throw; } // Deleted during refresh.
                    if (properties.Type == EntryTypeVpn)
                        result.Add(new VpnProfile(name, book, properties.Id, (entry->Flags & 1) != 0));
                });
            return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<IntPtr> FindConnections(VpnProfile profile)
        {
            var handles = new List<IntPtr>();
            Enumerate(sizeof(Connection), RasEnumConnectionsW, delegate(IntPtr address)
            {
                Connection* connection = (Connection*)address;
                if (!String.Equals(profile.Name, new string(connection->Name), StringComparison.OrdinalIgnoreCase)) return;
                if (profile.Id != Guid.Empty && connection->Id != Guid.Empty)
                {
                    if (profile.Id != connection->Id) return;
                }
                else if (!String.Equals(profile.Book ?? "", new string(connection->Book), StringComparison.OrdinalIgnoreCase)) return;
                handles.Add(connection->Handle);
            });
            return handles;
        }

        private static LinkState HandleState(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return LinkState.Down;
            var value = new ConnectionStatus { Size = (uint)sizeof(ConnectionStatus) };
            uint result = RasGetConnectStatusW(handle, (IntPtr)(&value));
            if (AlreadyDown(result)) return LinkState.Down;
            Check(result);
            if (value.State == Connected && value.Error == 0) return LinkState.Up;
            if (value.State == Disconnected || value.Error != 0) return LinkState.Disconnecting;
            return LinkState.Connecting;
        }

        public static LinkState State(VpnProfile profile)
        {
            LinkState state = LinkState.Down;
            foreach (IntPtr handle in FindConnections(profile))
            {
                LinkState current = HandleState(handle);
                if (current == LinkState.Up) return current;
                if (current == LinkState.Connecting || state == LinkState.Down) state = current;
            }
            return state;
        }

        public static void Disconnect(VpnProfile profile)
        {
            foreach (IntPtr handle in FindConnections(profile)) HangUp(handle);
        }

        public static bool ConnectWithDialog(VpnProfile profile, IntPtr owner)
        {
            var dialog = new DialDialog { Size = (uint)sizeof(DialDialog), Owner = owner };
            bool success = RasDialDlgW(profile.Book, profile.Name, null, (IntPtr)(&dialog));
            if (!success && dialog.Error != 0 && dialog.Error != 1223) Check(dialog.Error);
            return success;
        }

        private static IntPtr ReadDialParameters(VpnProfile profile, out bool saved)
        {
            var buffer = Marshal.AllocHGlobal(sizeof(DialParameters));
            try
            {
                byte[] zeros = new byte[sizeof(DialParameters)];
                Marshal.Copy(zeros, 0, buffer, zeros.Length);
                DialParameters* parameters = (DialParameters*)buffer;
                parameters->Size = (uint)sizeof(DialParameters);
                if (profile.Name.Length > 256) throw new RasFailure(623);
                for (int n = 0; n < profile.Name.Length; n++) parameters->Name[n] = profile.Name[n];
                int present;
                Check(RasGetEntryDialParamsW(profile.Book, buffer, out present));
                saved = present != 0;
                return buffer;
            }
            catch { FreeDialParameters(buffer); throw; }
        }

        private static void FreeDialParameters(IntPtr buffer)
        {
            // Windows returns an opaque password handle. Its value never enters managed strings or logs.
            var zeros = new byte[sizeof(DialParameters)];
            Marshal.Copy(zeros, 0, buffer, zeros.Length);
            Marshal.FreeHGlobal(buffer);
        }

        public static bool HasSavedPassword(VpnProfile profile)
        {
            bool saved;
            IntPtr buffer = ReadDialParameters(profile, out saved);
            FreeDialParameters(buffer);
            return saved;
        }

        public static DialAttempt StartDial(VpnProfile profile)
        {
            bool saved;
            IntPtr parameters = ReadDialParameters(profile, out saved);
            try
            {
                if (!saved) throw new RasFailure(RasFailure.NoSavedPassword);
                var id = new UIntPtr(unchecked((uint)Interlocked.Increment(ref nextCallback)));
                ((DialParameters*)parameters)->CallbackId = id;
                var attempt = new DialAttempt(id);
                if (!Attempts.TryAdd(id, attempt)) throw new InvalidOperationException("Повтор идентификатора попытки RAS.");
                try
                {
                    uint code = RasDialW(IntPtr.Zero, profile.Book, parameters, 2, Callback, out attempt.Handle);
                    Check(code);
                    if (attempt.Handle == IntPtr.Zero) throw new RasFailure(InvalidHandle);
                    return attempt;
                }
                catch { attempt.Cancel(false); throw; }
            }
            finally { FreeDialParameters(parameters); }
        }

        private static uint OnDial(UIntPtr id, uint subEntry, IntPtr handle, uint message, uint state, uint error, uint extended)
        {
            DialAttempt attempt;
            if (Attempts.TryGetValue(id, out attempt))
            {
                if (error != 0) attempt.SetResult(error);
                else if (state == Connected) attempt.SetResult(0);
                else if (state == Disconnected) attempt.SetResult(628);
            }
            return 1;
        }

        private static void HangUp(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            uint code = RasHangUpW(handle);
            if (!AlreadyDown(code)) Check(code);
        }

        private static bool AlreadyDown(uint code)
        {
            return code == InvalidHandle || code == 606 || code == 619 || code == 668 || code == 2250;
        }

        private static void Check(uint code) { if (code != 0) throw new RasFailure(code); }

        public static string ErrorText(uint code)
        {
            if (code == RasFailure.NoSavedPassword) return "Windows не сохранила пароль VPN. Подключитесь вручную и сохраните вход.";
            var text = new StringBuilder(512);
            string label = code > 65535 ? "0x" + code.ToString("X8") : code.ToString();
            if (RasGetErrorStringW(code, text, (uint)text.Capacity) != 0) return "Windows RAS: ошибка " + label + ".";
            return "Windows RAS " + label + ": " + text.ToString().Trim();
        }

        internal sealed class DialAttempt
        {
            private readonly UIntPtr id;
            private long result = -1;
            internal IntPtr Handle;

            internal DialAttempt(UIntPtr id) { this.id = id; }

            internal void SetResult(uint code) { Interlocked.CompareExchange(ref result, code, -1); }

            public long Result { get { return Interlocked.Read(ref result); } }

            public void Forget()
            {
                DialAttempt discarded;
                Attempts.TryRemove(id, out discarded);
            }

            public void Cancel(bool preserveConnected)
            {
                try { if (!preserveConnected || HandleState(Handle) != LinkState.Up) HangUp(Handle); }
                finally { Forget(); }
            }
        }
    }
}
