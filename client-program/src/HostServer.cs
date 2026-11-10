using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Tahakkum
{
    sealed class ConsentRequest
    {
        readonly ManualResetEvent done = new ManualResetEvent(false);
        int result;

        public string RemoteIp;
        public string Machine;
        public string User;
        public string Code;
        public string Nick = "";
        // How many people already watch this screen, so the person knows who else is there.
        public int Watching;
        public event Action Cancelled;

        public void Approve() { Resolve(1); }
        public void Reject() { Resolve(2); }

        public void Cancel()
        {
            Resolve(3);
        }

        void Resolve(int value)
        {
            if (Interlocked.CompareExchange(ref result, value, 0) != 0) return;
            done.Set();
            if (value == 3)
            {
                Action h = Cancelled;
                if (h != null) h();
            }
        }

        // 1 approved, 2 rejected, 3 cancelled by the requester or timed out.
        public int Wait(int ms)
        {
            if (!done.WaitOne(ms)) Cancel();
            return result;
        }
    }

    // Accepts connections. Nothing about the screen is sent and no input is accepted
    // until the person at this machine approves the request.
    sealed class HostServer
    {
        const int RejectCooldownSeconds = 15;
        const int PasswordFailsPerSource = 5;
        const int PasswordFailsTotal = 20;
        const int PasswordLockMinutes = 10;
        const string AnySource = "*";

        readonly X509Certificate2 certificate;
        readonly string fingerprint;
        readonly Dictionary<string, DateTime> cooldowns = new Dictionary<string, DateTime>();
        readonly Dictionary<string, List<DateTime>> passwordFails = new Dictionary<string, List<DateTime>>();
        TcpListener listener;
        int viewers;
        volatile bool running;

        public readonly string Code;
        public readonly ControlDesk Desk = new ControlDesk();
        // Set by the UI when the lock-screen service is installed; null otherwise.
        public LockScreenLink Lock;
        public event Action<ConsentRequest> ConsentNeeded;
        public event Action<HostSession> SessionStarted;
        public event Action<HostSession> SessionEnded;

        public HostServer(X509Certificate2 certificate)
        {
            this.certificate = certificate;
            fingerprint = Identity.Fingerprint(certificate);
            Code = Identity.VerificationCode(certificate);
        }

        public bool IsRunning { get { return running; } }
        public X509Certificate2 Certificate { get { return certificate; } }

        public void Start(int port)
        {
            if (running) return;
            TcpListener l = TcpListener.Create(port);
            l.ExclusiveAddressUse = true;
            l.Start(8);
            listener = l;
            running = true;
            Thread t = new Thread(() => AcceptLoop(l));
            t.IsBackground = true;
            t.Name = "host-accept";
            t.Start();
        }

        public void Stop()
        {
            running = false;
            TcpListener l = listener;
            listener = null;
            if (l != null)
            {
                try { l.Stop(); } catch (Exception) { }
            }
        }

        void AcceptLoop(TcpListener l)
        {
            while (running)
            {
                TcpClient client;
                try { client = l.AcceptTcpClient(); }
                catch (Exception) { return; }
                Thread t = new Thread(() => Handle(client, null, null));
                t.IsBackground = true;
                t.Name = "host-client";
                t.Start();
            }
        }

        // A viewer reached through the relay: same handshake, carried over the relay's stream.
        public void HandleRelayed(TcpClient tcp, Stream transport, string viewerIp)
        {
            Handle(tcp, transport, viewerIp);
        }

        // Failures count per source and in total, so spreading guesses over many addresses does not help.
        bool PasswordLocked(string ip)
        {
            lock (passwordFails)
                return RecentFails(ip) >= PasswordFailsPerSource || RecentFails(AnySource) >= PasswordFailsTotal;
        }

        int RecentFails(string key)
        {
            List<DateTime> list;
            if (!passwordFails.TryGetValue(key, out list)) return 0;
            DateTime since = DateTime.UtcNow.AddMinutes(-PasswordLockMinutes);
            list.RemoveAll(t => t < since);
            return list.Count;
        }

        void RecordPasswordFail(string ip)
        {
            lock (passwordFails)
            {
                foreach (string key in new[] { ip, AnySource })
                {
                    List<DateTime> list;
                    if (!passwordFails.TryGetValue(key, out list)) passwordFails[key] = list = new List<DateTime>();
                    list.Add(DateTime.UtcNow);
                }
            }
        }

        // Returns true for a correct password, false when the viewer asks for approval instead.
        bool ExchangePassword(Stream ssl, AccessKey key, string ip)
        {
            bool failed = false;
            for (int left = Proto.PasswordAttemptsPerConnection; left > 0; left--)
            {
                byte[] nonce = new byte[32];
                using (System.Security.Cryptography.RandomNumberGenerator rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(nonce);
                Connection.WritePacket(ssl, Msg.AuthChallenge, Pack.Build(w =>
                {
                    w.Write(key.Salt);
                    w.Write(key.Iterations);
                    w.Write(nonce);
                    w.Write(failed);
                }));
                Packet reply = Connection.ReadPacket(ssl, Proto.MaxPreApproval);
                if (reply.Type == Msg.AskConsent) return false;
                if (reply.Type != Msg.AuthProof) throw new InvalidDataException("auth");
                if (AccessPassword.SameBytes(reply.Data, AccessPassword.Proof(key.Key, nonce, fingerprint))) return true;

                RecordPasswordFail(ip);
                AppFiles.Log("كلمة سر خاطئة من " + ip);
                failed = true;
                if (PasswordLocked(ip)) break;
                Thread.Sleep(1000);
            }
            throw new PasswordRefused();
        }

        sealed class PasswordRefused : Exception { }

        bool InCooldown(string ip)
        {
            lock (cooldowns)
            {
                DateTime until;
                if (!cooldowns.TryGetValue(ip, out until)) return false;
                if (until > DateTime.UtcNow) return true;
                cooldowns.Remove(ip);
                return false;
            }
        }

        void Handle(TcpClient tcp, Stream transport, string relayedIp)
        {
            bool holdsBusy = false;
            string ip = "?";
            try
            {
                if (relayedIp != null)
                {
                    ip = relayedIp;
                }
                else
                {
                    IPAddress address = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address;
                    if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                    ip = address.ToString();
                }

                tcp.NoDelay = true;
                tcp.ReceiveTimeout = 10000;
                tcp.SendTimeout = 10000;
                tcp.SendBufferSize = 512 * 1024;

                // Through the relay this TLS runs inside the relay's own, so the relay only sees ciphertext.
                SslStream ssl = new SslStream(transport ?? tcp.GetStream(), false);
                ssl.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);

                Packet hello = Connection.ReadPacket(ssl, Proto.MaxPreApproval);
                if (hello.Type != Msg.Hello) throw new InvalidDataException("hello");
                string machine, user, nick = "";
                using (BinaryReader r = Pack.Read(hello.Data))
                {
                    if (r.ReadInt32() != Proto.Magic) throw new InvalidDataException("magic");
                    if (r.ReadInt32() != Proto.Version)
                    {
                        Connection.WritePacket(ssl, Msg.Denied, new[] { (byte)DenyReason.Version });
                        return;
                    }
                    machine = Identity.Sanitize(r.ReadString(), 64);
                    user = Identity.Sanitize(r.ReadString(), 64);
                    // The viewer's chosen name, which viewers from before this field do not send.
                    if (r.BaseStream.Position < r.BaseStream.Length) nick = Identity.Sanitize(r.ReadString(), Profile.MaxName).Trim();
                }

                // Only new sessions are refused: a session in progress when the license ends carries on.
                if (!LicenseManager.IsValid)
                {
                    AppFiles.Log("رُفض طلب من " + ip + " لأن رخصة هذا الجهاز غير سارية");
                    Connection.WritePacket(ssl, Msg.Denied, new[] { (byte)DenyReason.NotLicensed });
                    return;
                }

                bool byPassword = false;
                AccessKey key = AccessPassword.Load();
                if (key != null && !PasswordLocked(ip))
                {
                    tcp.ReceiveTimeout = 120000;
                    try
                    {
                        byPassword = ExchangePassword(ssl, key, ip);
                    }
                    catch (PasswordRefused)
                    {
                        Connection.WritePacket(ssl, Msg.Denied, new[] { (byte)DenyReason.BadPassword });
                        return;
                    }
                }

                if (!byPassword && InCooldown(ip))
                {
                    Connection.WritePacket(ssl, Msg.Denied, new[] { (byte)DenyReason.Cooldown });
                    return;
                }
                // A place is held from the request on, so prompts that are still open count too.
                if (Interlocked.Increment(ref viewers) > Proto.MaxViewers)
                {
                    Interlocked.Decrement(ref viewers);
                    Connection.WritePacket(ssl, Msg.Denied, new[] { (byte)DenyReason.Busy });
                    return;
                }
                holdsBusy = true;

                Connection conn = new Connection(tcp, ssl);
                ConsentRequest request = new ConsentRequest { RemoteIp = ip, Machine = machine, User = user, Nick = nick, Code = Code, Watching = Desk.Count };
                conn.Closed += request.Cancel;
                conn.SetTimeouts((Proto.ConsentSeconds + 15) * 1000, 15000);
                conn.Start();
                conn.Send(Msg.Pending, Pack.Build(w => w.Write(Environment.MachineName)));

                if (!byPassword)
                {
                    AppFiles.Log("طلب اتصال من " + ip + " (" + user + "@" + machine + ")");
                    Action<ConsentRequest> needed = ConsentNeeded;
                    if (needed != null) needed(request);

                    int outcome = request.Wait(Proto.ConsentSeconds * 1000);
                    if (outcome != 1)
                    {
                        if (outcome == 2)
                        {
                            lock (cooldowns) cooldowns[ip] = DateTime.UtcNow.AddSeconds(RejectCooldownSeconds);
                        }
                        AppFiles.Log((outcome == 2 ? "رُفض الطلب من " : "انتهت مهلة الطلب أو أُلغي من ") + ip);
                        conn.SendAndClose(Msg.Denied, new[] { (byte)(outcome == 2 ? DenyReason.Rejected : DenyReason.Timeout) });
                        return;
                    }
                }

                AppFiles.Log((byPassword ? "بدأت جلسة تحكم بكلمة السر من " : "بدأت جلسة تحكم من ") + ip + " (" + (nick.Length > 0 ? nick + "، " : "") + user + "@" + machine + ")");
                conn.MaxIncoming = Proto.MaxFromViewer;
                conn.SetTimeouts(15000, 15000);
                HostSession session = new HostSession(conn, ip, machine, user, nick, Desk, byPassword, Lock);
                Action<HostSession> started = SessionStarted;
                if (started != null) started(session);
                try
                {
                    session.Run();
                }
                finally
                {
                    Action<HostSession> ended = SessionEnded;
                    if (ended != null) ended(session);
                    AppFiles.Log("انتهت جلسة التحكم من " + ip);
                }
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is AuthenticationException || ex is ObjectDisposedException || ex is InvalidDataException || ex is EndOfStreamException))
                    AppFiles.Log("خطأ في الاتصال من " + ip + ": " + ex.GetType().Name + " " + ex.Message);
                try { tcp.Close(); } catch (Exception) { }
            }
            finally
            {
                if (holdsBusy) Interlocked.Decrement(ref viewers);
            }
        }
    }

    // One approved control session: streams the selected monitor and replays input.
    sealed class HostSession
    {
        const int MaxInFlight = 2;
        const int MinFrameMs = 33;
        const int RefineAfterMs = 350;
        const int SharpQuality = 92;

        readonly Connection conn;
        readonly InputInjector injector = new InputInjector();
        readonly AutoResetEvent ackSignal = new AutoResetEvent(false);
        readonly long[] sentAt = new long[16];
        readonly Stopwatch clock = Stopwatch.StartNew();

        public readonly string RemoteIp;
        public readonly string Machine;
        public readonly string User;
        public readonly string Nick;
        public readonly FileTransferService Files;
        readonly ControlDesk desk;
        // Only a password-authenticated viewer may reach the secure (lock) screen, through the agent.
        readonly bool byPassword;
        readonly LockScreenLink lockLink;
        Action<byte[]> onLockInfo;
        Action<byte[]> onLockFrame;

        volatile bool stopping;
        volatile bool hasControl;
        volatile bool paused;
        volatile bool locked;
        volatile bool secureRelay;
        volatile int qualityMode;
        volatile int requestedMonitor = -1;
        volatile bool displaysChanged;
        int lastAcked;
        int autoQuality = 70;

        public event Action<string> ClipboardFromViewer;

        public HostSession(Connection conn, string ip, string machine, string user, string nick, ControlDesk desk, bool byPassword, LockScreenLink lockLink)
        {
            this.conn = conn;
            this.desk = desk;
            this.byPassword = byPassword;
            this.lockLink = lockLink;
            RemoteIp = ip;
            Machine = machine;
            User = user;
            Nick = nick ?? "";
            Files = new FileTransferService(conn, ip);
            // Nobody can answer a prompt on a locked machine, and pausing means the viewer should stop acting here.
            Files.OffersBlocked = () => paused || locked;
        }

        public bool Paused { get { return paused; } }
        public bool HasControl { get { return hasControl; } }

        // Who this is, for the person at the machine: the name they chose, else their Windows user.
        public string DisplayName
        {
            get { return Nick.Length > 0 ? Nick : User.Length > 0 ? User : RemoteIp; }
        }

        // Called by the desk, which keeps every viewer's view of who controls consistent.
        public void ApplyControl(bool mine, string controller, string[] names)
        {
            bool had = hasControl;
            hasControl = mine;
            if (had && !mine) injector.ReleaseAll();
            conn.Send(Msg.ControlState, Pack.Build(w =>
            {
                w.Write(mine);
                w.Write(controller);
                w.Write(names.Length);
                foreach (string n in names) w.Write(n);
            }));
        }

        public void SendClipboard(string text)
        {
            conn.Send(Msg.Clipboard, Pack.Build(w => w.Write(text)));
        }

        public void SetPaused(bool value)
        {
            paused = value;
            if (value) injector.ReleaseAll();
            conn.Send(Msg.Status, new[] { (byte)(value ? HostStatus.ControlPaused : HostStatus.ControlResumed) });
        }

        public void End()
        {
            stopping = true;
            conn.SendAndClose(Msg.Bye, null);
        }

        public void NotifyDisplaysChanged()
        {
            displaysChanged = true;
        }

        public void Run()
        {
            conn.Received += OnMessage;
            conn.Closed += () => { stopping = true; ackSignal.Set(); };
            conn.Send(Msg.Approved, null);
            desk.Join(this);
            try
            {
                CaptureLoop();
            }
            finally
            {
                ExitSecure();
                desk.Leave(this);
                hasControl = false;
                injector.ReleaseAll();
                conn.Close();
                Files.Shutdown();
            }
        }

        // Start relaying the agent's secure-desktop frames to this viewer. Idempotent.
        void EnterSecure()
        {
            if (secureRelay) return;
            secureRelay = true;
            onLockInfo = p => { try { conn.Send(Msg.ScreenInfo, p); } catch (Exception) { } };
            onLockFrame = p => { try { conn.Send(Msg.Frame, p); } catch (Exception) { } };
            lockLink.ScreenInfoReceived += onLockInfo;
            lockLink.FrameReceived += onLockFrame;
            conn.Send(Msg.Status, new[] { (byte)HostStatus.Normal });
            byte[] info = lockLink.LastScreenInfo;
            if (info != null) conn.Send(Msg.ScreenInfo, info);
        }

        void ExitSecure()
        {
            if (!secureRelay) return;
            secureRelay = false;
            if (lockLink != null)
            {
                if (onLockInfo != null) lockLink.ScreenInfoReceived -= onLockInfo;
                if (onLockFrame != null) lockLink.FrameReceived -= onLockFrame;
            }
            onLockInfo = null;
            onLockFrame = null;
        }

        void OnMessage(Msg type, byte[] data)
        {
            if (Files.Handle(type, data)) return;
            using (BinaryReader r = Pack.Read(data))
            {
                switch (type)
                {
                    case Msg.Ack:
                        int seq = r.ReadInt32();
                        Interlocked.Exchange(ref lastAcked, seq);
                        UpdateAutoQuality(seq);
                        ackSignal.Set();
                        return;
                    case Msg.Ping:
                        conn.Send(Msg.Pong, data);
                        return;
                    case Msg.SelectMonitor:
                        requestedMonitor = r.ReadInt32();
                        return;
                    case Msg.SetQuality:
                        qualityMode = Math.Max(0, Math.Min(3, r.ReadInt32()));
                        return;
                    case Msg.Clipboard:
                        string text = r.ReadString();
                        Action<string> h = ClipboardFromViewer;
                        if (h != null && text.Length <= Proto.MaxClipboardChars) h(text);
                        return;
                    case Msg.Bye:
                        stopping = true;
                        conn.Close();
                        return;
                    case Msg.ControlRequest:
                        desk.Give(this);
                        return;
                }

                // Only the viewer holding control drives this machine; the others watch.
                if (stopping || paused || !hasControl) return;
                // When locked, input goes to the secure desktop through the agent, or nowhere.
                bool secureInput = secureRelay && lockLink != null;
                if (locked && !secureInput) return;
                switch (type)
                {
                    case Msg.MouseMove:
                    {
                        int x = r.ReadInt32(), y = r.ReadInt32();
                        if (secureInput) lockLink.SendInput(Pack.Build(w => { w.Write(LockMsg.InMove); w.Write(x); w.Write(y); }));
                        else injector.Move(x, y);
                        break;
                    }
                    case Msg.MouseButton:
                    {
                        byte button = r.ReadByte();
                        bool down = r.ReadBoolean();
                        int x = r.ReadInt32(), y = r.ReadInt32();
                        if (secureInput) lockLink.SendInput(Pack.Build(w => { w.Write(LockMsg.InButton); w.Write(button); w.Write(down); w.Write(x); w.Write(y); }));
                        else { injector.Move(x, y); injector.Button(button, down); }
                        break;
                    }
                    case Msg.MouseWheel:
                    {
                        int delta = r.ReadInt32();
                        bool horizontal = r.ReadBoolean();
                        if (secureInput) lockLink.SendInput(Pack.Build(w => { w.Write(LockMsg.InWheel); w.Write(delta); w.Write(horizontal); }));
                        else injector.Wheel(delta, horizontal);
                        break;
                    }
                    case Msg.Key:
                    {
                        int vk = r.ReadInt32();
                        int scan = r.ReadInt32();
                        bool extended = r.ReadBoolean();
                        bool kdown = r.ReadBoolean();
                        // Older viewers do not send the character; only read it when it is there.
                        int ch = (r.BaseStream.Position + 2 <= r.BaseStream.Length) ? r.ReadUInt16() : 0;
                        if (secureInput) lockLink.SendInput(Pack.Build(w => { w.Write(LockMsg.InKey); w.Write(vk); w.Write(scan); w.Write(extended); w.Write(kdown); w.Write((ushort)ch); }));
                        else injector.Key(vk, scan, extended, kdown);
                        break;
                    }
                }
            }
        }

        void UpdateAutoQuality(int seq)
        {
            long rtt = clock.ElapsedMilliseconds - Interlocked.Read(ref sentAt[seq & 15]);
            int q = autoQuality;
            if (rtt > 180) q = Math.Max(35, q - 5);
            else if (rtt < 60) q = Math.Min(75, q + 1);
            autoQuality = q;
        }

        int FrameQuality()
        {
            switch (qualityMode)
            {
                case 1: return 85;
                case 2: return 60;
                case 3: return 35;
                default: return autoQuality;
            }
        }

        static bool IsDesktopReachable()
        {
            IntPtr desk = Native.OpenInputDesktop(0, false, 0x0100);
            if (desk == IntPtr.Zero) return false;
            bool ok = Native.SwitchDesktop(desk);
            Native.CloseDesktop(desk);
            return ok;
        }

        void SendScreenInfo(Screen[] screens, int current, Rectangle bounds)
        {
            conn.Send(Msg.ScreenInfo, Pack.Build(w =>
            {
                w.Write(current);
                w.Write(bounds.Width);
                w.Write(bounds.Height);
                w.Write(screens.Length);
                foreach (Screen s in screens)
                {
                    w.Write(s.Bounds.Width);
                    w.Write(s.Bounds.Height);
                    w.Write(s.Primary);
                }
            }));
        }

        void CaptureLoop()
        {
            ScreenCapturer capturer = null;
            int monitor = -1;
            int seq = 0;
            bool force = true;
            long lastChange = 0;
            long lastDesktopCheck = -1000;
            long lastBoundsCheck = 0;
            Screen[] screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
                if (screens[i].Primary) monitor = i;
            if (monitor < 0) monitor = 0;

            try
            {
                while (!stopping)
                {
                    long now = clock.ElapsedMilliseconds;

                    if (now - lastDesktopCheck >= 500)
                    {
                        lastDesktopCheck = now;
                        bool reachable = IsDesktopReachable();
                        if (reachable == locked)
                        {
                            locked = !reachable;
                            if (locked) injector.ReleaseAll();
                            else ExitSecure();
                            conn.Send(Msg.Status, new[] { (byte)(locked ? HostStatus.Locked : HostStatus.Normal) });
                            force = true;
                        }
                    }
                    if (locked)
                    {
                        // While locked, a password-authenticated viewer sees and drives the secure
                        // desktop through the SYSTEM agent; everyone else just sees the locked notice.
                        bool secure = byPassword && lockLink != null && lockLink.SecureActive;
                        if (secure) EnterSecure(); else ExitSecure();
                        Thread.Sleep(secure ? 120 : 200);
                        continue;
                    }

                    if (now - lastBoundsCheck >= 1000)
                    {
                        lastBoundsCheck = now;
                        Screen[] latest = Screen.AllScreens;
                        if (latest.Length != screens.Length || (monitor < latest.Length && capturer != null && latest[monitor].Bounds != capturer.Bounds))
                            displaysChanged = true;
                    }

                    int wanted = requestedMonitor;
                    if (wanted >= 0 || displaysChanged || capturer == null)
                    {
                        requestedMonitor = -1;
                        displaysChanged = false;
                        screens = Screen.AllScreens;
                        if (wanted >= 0 && wanted < screens.Length) monitor = wanted;
                        if (monitor >= screens.Length) monitor = 0;
                        if (capturer != null) capturer.Dispose();
                        capturer = new ScreenCapturer(screens[monitor].Bounds);
                        injector.SetMonitor(screens[monitor].Bounds);
                        SendScreenInfo(screens, monitor, capturer.Bounds);
                        force = true;
                    }

                    if (seq - Volatile.Read(ref lastAcked) >= MaxInFlight)
                    {
                        ackSignal.WaitOne(100);
                        continue;
                    }

                    if (!capturer.Grab())
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    List<Rectangle> rects = capturer.Diff(force);
                    int quality = FrameQuality();
                    if (rects.Count > 0)
                    {
                        lastChange = now;
                        if (force) force = false;
                    }
                    else if (now - lastChange >= RefineAfterMs && capturer.HasPendingRefine)
                    {
                        rects = capturer.TakeRefine();
                        quality = qualityMode == 3 ? 60 : SharpQuality;
                    }

                    if (rects.Count > 0)
                    {
                        seq++;
                        Interlocked.Exchange(ref sentAt[seq & 15], clock.ElapsedMilliseconds);
                        conn.Send(Msg.Frame, BuildFrame(capturer, rects, seq, quality));
                    }

                    int spent = (int)(clock.ElapsedMilliseconds - now);
                    if (spent < MinFrameMs) Thread.Sleep(MinFrameMs - spent);
                }
            }
            finally
            {
                if (capturer != null) capturer.Dispose();
            }
        }

        static byte[] BuildFrame(ScreenCapturer capturer, List<Rectangle> rects, int seq, int quality)
        {
            byte[][] encoded = new byte[rects.Count][];
            Parallel.For(0, rects.Count, i => { encoded[i] = capturer.Encode(rects[i], quality); });
            return Pack.Build(w =>
            {
                w.Write(seq);
                w.Write(rects.Count);
                for (int i = 0; i < rects.Count; i++)
                {
                    w.Write(rects[i].X);
                    w.Write(rects[i].Y);
                    w.Write(rects[i].Width);
                    w.Write(rects[i].Height);
                    w.Write(encoded[i].Length);
                    w.Write(encoded[i]);
                }
            });
        }
    }

    // Decides which connected viewer drives the mouse and keyboard. Everyone sees the screen,
    // one person controls at a time, and every viewer always hears who that is.
    sealed class ControlDesk
    {
        readonly object gate = new object();
        readonly List<HostSession> sessions = new List<HostSession>();
        HostSession controller;

        public event Action Changed;

        public int Count
        {
            get { lock (gate) return sessions.Count; }
        }

        public HostSession Controller
        {
            get { lock (gate) return controller; }
        }

        public HostSession[] Sessions
        {
            get { lock (gate) return sessions.ToArray(); }
        }

        // The first to arrive controls, so a single viewer works exactly as before.
        public void Join(HostSession s)
        {
            lock (gate)
            {
                if (sessions.Contains(s)) return;
                sessions.Add(s);
                if (controller == null) controller = s;
                Publish();
            }
            Raise();
        }

        // When the controller leaves, control passes to whoever has been here longest.
        public void Leave(HostSession s)
        {
            lock (gate)
            {
                if (!sessions.Remove(s)) return;
                if (controller == s) controller = sessions.Count > 0 ? sessions[0] : null;
                Publish();
            }
            Raise();
        }

        // Used both by a viewer taking control and by the person at the machine handing it over.
        public void Give(HostSession s)
        {
            lock (gate)
            {
                if (!sessions.Contains(s) || controller == s) return;
                controller = s;
                AppFiles.Log("انتقل التحكم إلى " + s.DisplayName + " (" + s.RemoteIp + ")");
                Publish();
            }
            Raise();
        }

        // Runs under the lock, so two changes can never reach viewers out of order.
        void Publish()
        {
            string name = controller != null ? controller.DisplayName : "";
            string[] names = sessions.Select(x => x.DisplayName).ToArray();
            foreach (HostSession s in sessions) s.ApplyControl(s == controller, name, names);
        }

        void Raise()
        {
            Action h = Changed;
            if (h != null) h();
        }
    }
}
