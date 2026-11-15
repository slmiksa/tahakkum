using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Tahakkum
{
    enum FileAnswerCode : byte { Accepted = 1, Rejected = 2, Busy = 3, Blocked = 4, NoSpace = 5 }

    enum TransferState { Waiting, Running, Completed, Failed }

    // Files offered by the other side, waiting for the person here to answer.
    sealed class FileOffer
    {
        public int Id;
        public string[] Names;
        public long[] Sizes;
        public long Total;
        public event Action Cancelled;

        public void RaiseCancelled()
        {
            Action h = Cancelled;
            if (h != null) h();
        }
    }

    // One batch of files in one direction. Read by the UI, updated from the network threads.
    sealed class FileTransfer
    {
        long done;

        public bool Incoming;
        public string Title;
        public long Total;
        public volatile TransferState State;
        public volatile string Message;
        public volatile string LastPath;
        public Action Cancel;

        public long Done { get { return Interlocked.Read(ref done); } }
        public void Add(long n) { Interlocked.Add(ref done, n); }
        public bool Active { get { return State == TransferState.Waiting || State == TransferState.Running; } }
    }

    // Moves files both ways over the session's connection. Nothing is written to disk until
    // the receiver accepts the offer, and received files never replace an existing file.
    sealed class FileTransferService
    {
        sealed class Outgoing
        {
            public int Id;
            public string[] Paths;
            public long[] Sizes;
            public FileTransfer Transfer;
            public bool Accepted;
            public bool Stopped;
            public int InFlight;
        }

        sealed class Incoming
        {
            public FileOffer Offer;
            public FileTransfer Transfer;
            public int Index;
            public long Received;
            public FileStream Stream;
            public string PartPath;
        }

        readonly Connection conn;
        readonly string peer;
        readonly object gate = new object();
        int nextId = 1;
        FileOffer pending;
        Outgoing outgoing;
        Incoming incoming;
        bool shutDown;

        public Func<bool> OffersBlocked;
        // When this returns true, an incoming offer is accepted automatically, without asking the
        // person at this machine — the dashboard's per-device "auto file transfer" entitlement.
        public Func<bool> AutoAccept;
        public event Action<FileOffer> OfferReceived;
        public event Action<FileTransfer> TransferStarted;

        public FileTransferService(Connection conn, string peer)
        {
            this.conn = conn;
            this.peer = peer;
            conn.Closed += Shutdown;
        }

        public string SaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Tahakkum");

        public bool Sending
        {
            get { lock (gate) return outgoing != null; }
        }

        // ---- sending ----

        // Returns an error for the person, or null when the offer went out.
        public string Send(string[] paths)
        {
            if (paths.Length == 0) return null;
            if (paths.Length > Proto.MaxFilesPerOffer) return "يمكن إرسال " + Proto.MaxFilesPerOffer + " ملف كحد أقصى في المرة الواحدة.";
            long[] sizes = new long[paths.Length];
            string[] names = new string[paths.Length];
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    FileInfo info = new FileInfo(paths[i]);
                    sizes[i] = info.Length;
                    names[i] = info.Name;
                }
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                    return "تعذرت قراءة أحد الملفات المحددة.";
                throw;
            }

            Outgoing o = new Outgoing { Paths = paths, Sizes = sizes };
            o.Transfer = new FileTransfer
            {
                Title = paths.Length == 1 ? names[0] : paths.Length + " ملفات",
                Total = sizes.Sum(),
                State = TransferState.Waiting,
                Message = "بانتظار موافقة الطرف الآخر…",
                Cancel = () => CancelOutgoing(o)
            };
            lock (gate)
            {
                if (shutDown) return "انقطع الاتصال بالجهاز.";
                if (outgoing != null) return "هناك إرسال جارٍ. انتظر حتى يكتمل.";
                o.Id = nextId;
                nextId++;
                outgoing = o;
            }
            conn.Send(Msg.FileOffer, Pack.Build(w =>
            {
                w.Write(o.Id);
                w.Write(names.Length);
                for (int i = 0; i < names.Length; i++)
                {
                    w.Write(names[i]);
                    w.Write(sizes[i]);
                }
            }));
            AppFiles.Log("عرض إرسال إلى " + peer + ": " + string.Join("، ", names));

            Thread t = new Thread(() => SendLoop(o));
            t.IsBackground = true;
            t.Name = "file-send";
            t.Start();
            Raise(o.Transfer);
            return null;
        }

        void SendLoop(Outgoing o)
        {
            FileTransfer t = o.Transfer;
            try
            {
                lock (gate)
                {
                    while (!o.Accepted && !o.Stopped) Monitor.Wait(gate);
                    if (o.Stopped) return;
                }
                t.State = TransferState.Running;
                t.Message = null;

                byte[] buffer = new byte[Proto.FileChunk];
                for (int i = 0; i < o.Paths.Length; i++)
                {
                    using (FileStream fs = new FileStream(o.Paths[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long left = o.Sizes[i];
                        // Every file sends at least one chunk, so an empty file still gets created.
                        do
                        {
                            lock (gate)
                            {
                                while (o.InFlight >= Proto.FileWindow && !o.Stopped) Monitor.Wait(gate);
                                if (o.Stopped) return;
                                o.InFlight++;
                            }
                            int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                            if (n <= 0 && left > 0) throw new IOException("file shrank");
                            byte[] packet = new byte[4 + n];
                            Buffer.BlockCopy(BitConverter.GetBytes(o.Id), 0, packet, 0, 4);
                            Buffer.BlockCopy(buffer, 0, packet, 4, n);
                            conn.Send(Msg.FileData, packet);
                            left -= n;
                        }
                        while (left > 0);
                    }
                }

                // The transfer is done only once the receiver has acknowledged every chunk.
                lock (gate)
                {
                    while (o.InFlight > 0 && !o.Stopped) Monitor.Wait(gate);
                    if (o.Stopped) return;
                    if (outgoing == o) outgoing = null;
                }
                t.State = TransferState.Completed;
                t.Message = "اكتمل الإرسال.";
                AppFiles.Log("اكتمل إرسال " + t.Title + " إلى " + peer);
            }
            catch (Exception ex)
            {
                // This is a background thread: letting an exception escape would end the whole program.
                if (!(ex is IOException || ex is UnauthorizedAccessException)) AppFiles.Log("خطأ أثناء إرسال ملف: " + ex);
                FailOutgoing(o, "تعذرت قراءة الملف أثناء الإرسال.", true);
            }
        }

        void CancelOutgoing(Outgoing o)
        {
            FailOutgoing(o, "تم إلغاء الإرسال.", true);
        }

        void FailOutgoing(Outgoing o, string message, bool tellPeer)
        {
            lock (gate)
            {
                if (o.Stopped || outgoing != o) return;
                o.Stopped = true;
                outgoing = null;
                Monitor.PulseAll(gate);
            }
            o.Transfer.State = TransferState.Failed;
            o.Transfer.Message = message;
            AppFiles.Log("توقف إرسال " + o.Transfer.Title + " إلى " + peer + ": " + message);
            if (tellPeer) SendCancel(o.Id, true);
        }

        void SendCancel(int id, bool bySender)
        {
            conn.Send(Msg.FileCancel, Pack.Build(w => { w.Write(id); w.Write(bySender); }));
        }

        // ---- receiving ----

        void HandleOffer(BinaryReader r)
        {
            int id = r.ReadInt32();
            int count = r.ReadInt32();
            if (count <= 0 || count > Proto.MaxFilesPerOffer) throw new InvalidDataException("file count");
            FileOffer offer = new FileOffer { Id = id, Names = new string[count], Sizes = new long[count] };
            for (int i = 0; i < count; i++)
            {
                offer.Names[i] = SafeFileName(r.ReadString());
                offer.Sizes[i] = r.ReadInt64();
                if (offer.Sizes[i] < 0 || offer.Sizes[i] > long.MaxValue - offer.Total) throw new InvalidDataException("file size");
                offer.Total += offer.Sizes[i];
            }

            Func<bool> blocked = OffersBlocked;
            Action<FileOffer> received = OfferReceived;
            FileAnswerCode? refuse = null;
            if (blocked != null && blocked()) refuse = FileAnswerCode.Blocked;
            else if (received == null) refuse = FileAnswerCode.Busy;
            else if (!HasRoomFor(offer.Total)) refuse = FileAnswerCode.NoSpace;
            lock (gate)
            {
                if (shutDown) return;
                if (refuse == null && (pending != null || incoming != null)) refuse = FileAnswerCode.Busy;
                if (refuse == null) pending = offer;
            }
            if (refuse != null)
            {
                SendAnswer(id, refuse.Value);
                return;
            }
            AppFiles.Log("طلب إرسال ملفات من " + peer + ": " + string.Join("، ", offer.Names));
            // A device with the auto-file-transfer entitlement accepts without asking its user.
            Func<bool> auto = AutoAccept;
            if (auto != null && auto())
            {
                AppFiles.Log("قبول تلقائي لملفات من " + peer + " (صلاحية نقل الملفات التلقائي)");
                Accept(offer);
                return;
            }
            received(offer);
        }

        bool HasRoomFor(long total)
        {
            try
            {
                string root = Path.GetPathRoot(SaveFolder);
                return new DriveInfo(root).AvailableFreeSpace > total;
            }
            catch (Exception)
            {
                // A network home folder has no drive letter; a real shortage shows up as a write error.
                return true;
            }
        }

        void SendAnswer(int id, FileAnswerCode code)
        {
            conn.Send(Msg.FileAnswer, Pack.Build(w => { w.Write(id); w.Write((byte)code); }));
        }

        public void Reject(FileOffer offer)
        {
            lock (gate)
            {
                if (pending != offer) return;
                pending = null;
            }
            AppFiles.Log("رُفضت ملفات من " + peer);
            SendAnswer(offer.Id, FileAnswerCode.Rejected);
        }

        public void Accept(FileOffer offer)
        {
            Incoming job = new Incoming { Offer = offer };
            job.Transfer = new FileTransfer
            {
                Incoming = true,
                Title = offer.Names.Length == 1 ? offer.Names[0] : offer.Names.Length + " ملفات",
                Total = offer.Total,
                State = TransferState.Running,
                Cancel = () => FailIncoming(job, "تم إلغاء الاستلام.", true)
            };
            bool opened = false;
            lock (gate)
            {
                if (pending != offer) return;
                pending = null;
                try
                {
                    Directory.CreateDirectory(SaveFolder);
                    OpenPart(job);
                    incoming = job;
                    opened = true;
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                }
            }
            if (!opened)
            {
                AppFiles.Log("تعذر إنشاء مجلد الاستلام " + SaveFolder);
                SendAnswer(offer.Id, FileAnswerCode.NoSpace);
                return;
            }
            AppFiles.Log("قُبلت ملفات من " + peer);
            SendAnswer(offer.Id, FileAnswerCode.Accepted);
            Raise(job.Transfer);
        }

        // The partial file gets a private name, so a half-received file can never be opened by mistake.
        void OpenPart(Incoming job)
        {
            job.PartPath = Path.Combine(SaveFolder, ".tahakkum-" + Guid.NewGuid().ToString("N") + ".part");
            job.Stream = new FileStream(job.PartPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            job.Received = 0;
        }

        void HandleData(byte[] data)
        {
            if (data.Length < 4) throw new InvalidDataException("file chunk");
            int id = BitConverter.ToInt32(data, 0);
            int len = data.Length - 4;
            Incoming job;
            string failure = null;
            lock (gate)
            {
                job = incoming;
                // A chunk already on the wire when a cancel crossed it is simply dropped.
                if (job == null || job.Offer.Id != id) return;
                try
                {
                    long size = job.Offer.Sizes[job.Index];
                    if (len > size - job.Received)
                    {
                        failure = "أرسل الطرف الآخر بيانات أكثر من حجم الملف.";
                    }
                    else
                    {
                        job.Stream.Write(data, 4, len);
                        job.Received += len;
                        job.Transfer.Add(len);
                        if (job.Received == size)
                        {
                            FinishFile(job);
                            job.Index++;
                            if (job.Index < job.Offer.Names.Length)
                            {
                                OpenPart(job);
                            }
                            else
                            {
                                incoming = null;
                                job.Transfer.State = TransferState.Completed;
                                job.Transfer.Message = Lang.L("تم الحفظ في مجلد التنزيلات\\Tahakkum.", "Saved to the Downloads\\Tahakkum folder.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException)) throw;
                    failure = "تعذرت الكتابة على القرص. قد تكون المساحة غير كافية.";
                }
            }
            if (failure != null)
            {
                FailIncoming(job, failure, true);
                return;
            }
            conn.Send(Msg.FileAck, Pack.Build(w => { w.Write(id); w.Write(len); }));
        }

        void FinishFile(Incoming job)
        {
            job.Stream.Dispose();
            job.Stream = null;
            string name = job.Offer.Names[job.Index];
            string final = MoveToFreeName(job.PartPath, name);
            job.PartPath = null;
            job.Transfer.LastPath = final;
            AppFiles.Log("استُلم من " + peer + ": " + final);
        }

        // File.Move fails if the target exists, so a received file can never overwrite one.
        string MoveToFreeName(string part, string name)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            for (int n = 0; n < 10000; n++)
            {
                string target = Path.Combine(SaveFolder, n == 0 ? name : stem + " (" + n + ")" + ext);
                if (File.Exists(target) || Directory.Exists(target)) continue;
                try
                {
                    File.Move(part, target);
                    return target;
                }
                catch (IOException)
                {
                    if (!File.Exists(target)) throw;
                }
            }
            throw new IOException("no free name");
        }

        void FailIncoming(Incoming job, string message, bool tellPeer)
        {
            lock (gate)
            {
                if (incoming != job) return;
                incoming = null;
                DiscardPart(job);
            }
            job.Transfer.State = TransferState.Failed;
            job.Transfer.Message = message;
            AppFiles.Log("توقف استلام " + job.Transfer.Title + " من " + peer + ": " + message);
            if (tellPeer) SendCancel(job.Offer.Id, false);
        }

        static void DiscardPart(Incoming job)
        {
            try
            {
                if (job.Stream != null) job.Stream.Dispose();
                job.Stream = null;
                if (job.PartPath != null) File.Delete(job.PartPath);
                job.PartPath = null;
            }
            catch (Exception) { }
        }

        // Names come from the remote side. Separators become "_" so a name cannot leave the
        // save folder, and bidi characters are stripped so an extension cannot be disguised.
        public static string SafeFileName(string raw)
        {
            string s = Identity.Sanitize(raw, 512);
            StringBuilder b = new StringBuilder();
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in s) b.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            s = b.ToString().Trim().TrimEnd('.', ' ');
            if (s.Length == 0) return "file";

            int dot = s.LastIndexOf('.');
            string stem = dot > 0 ? s.Substring(0, dot) : s;
            string ext = dot > 0 ? s.Substring(dot) : "";
            if (ext.Length > 20) { stem = s; ext = ""; }
            if (stem.Length > 150) stem = stem.Substring(0, 150);

            // Windows reserves these device names whatever the extension is.
            string device = stem.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL"
                || (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && char.IsDigit(device[3])))
                stem = "_" + stem;
            return stem + ext;
        }

        // ---- messages ----

        // Returns false when the message is not about files.
        public bool Handle(Msg type, byte[] data)
        {
            switch (type)
            {
                case Msg.FileOffer:
                    using (BinaryReader r = Pack.Read(data)) HandleOffer(r);
                    return true;
                case Msg.FileData:
                    HandleData(data);
                    return true;
                case Msg.FileAnswer:
                    using (BinaryReader r = Pack.Read(data)) HandleAnswer(r.ReadInt32(), (FileAnswerCode)r.ReadByte());
                    return true;
                case Msg.FileAck:
                    using (BinaryReader r = Pack.Read(data)) HandleAck(r.ReadInt32(), r.ReadInt32());
                    return true;
                case Msg.FileCancel:
                    using (BinaryReader r = Pack.Read(data))
                    {
                        int id = r.ReadInt32();
                        HandleCancel(id, r.ReadBoolean());
                    }
                    return true;
            }
            return false;
        }

        void HandleAnswer(int id, FileAnswerCode code)
        {
            Outgoing o;
            lock (gate)
            {
                o = outgoing;
                if (o == null || o.Id != id || o.Accepted) return;
                if (code == FileAnswerCode.Accepted)
                {
                    o.Accepted = true;
                    Monitor.PulseAll(gate);
                    return;
                }
            }
            FailOutgoing(o, AnswerText(code), false);
        }

        static string AnswerText(FileAnswerCode code)
        {
            switch (code)
            {
                case FileAnswerCode.Busy: return "الطرف الآخر يستلم ملفات أخرى الآن. حاول بعد اكتمالها.";
                case FileAnswerCode.Blocked: return "الجهاز الآخر مقفل أو أوقف التحكم مؤقتاً.";
                case FileAnswerCode.NoSpace: return "لا توجد مساحة كافية أو تعذر الحفظ على الجهاز الآخر.";
                default: return "رفض الطرف الآخر استلام الملفات.";
            }
        }

        void HandleAck(int id, int bytes)
        {
            lock (gate)
            {
                Outgoing o = outgoing;
                if (o == null || o.Id != id || o.InFlight <= 0) return;
                o.InFlight--;
                if (bytes > 0) o.Transfer.Add(Math.Min(bytes, Proto.FileChunk));
                Monitor.PulseAll(gate);
            }
        }

        void HandleCancel(int id, bool bySender)
        {
            if (bySender)
            {
                FileOffer offer = null;
                Incoming job;
                lock (gate)
                {
                    if (pending != null && pending.Id == id)
                    {
                        offer = pending;
                        pending = null;
                    }
                    job = incoming;
                }
                if (offer != null) offer.RaiseCancelled();
                else if (job != null && job.Offer.Id == id) FailIncoming(job, "ألغى الطرف الآخر الإرسال.", false);
            }
            else
            {
                Outgoing o;
                lock (gate) o = outgoing;
                if (o != null && o.Id == id) FailOutgoing(o, "ألغى الطرف الآخر الاستلام.", false);
            }
        }

        // Stops everything when the session ends. Files already completed stay; a partial one is removed.
        public void Shutdown()
        {
            FileOffer offer;
            Outgoing o;
            Incoming job;
            lock (gate)
            {
                shutDown = true;
                offer = pending;
                pending = null;
                o = outgoing;
                job = incoming;
            }
            if (offer != null) offer.RaiseCancelled();
            if (o != null) FailOutgoing(o, "انقطع الاتصال قبل اكتمال الإرسال.", false);
            if (job != null) FailIncoming(job, "انقطع الاتصال قبل اكتمال الاستلام.", false);
        }

        void Raise(FileTransfer t)
        {
            Action<FileTransfer> h = TransferStarted;
            if (h != null) h(t);
        }

        public static string FormatSize(long bytes)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (bytes < 1024) return bytes.ToString(c) + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0", c) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", c) + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", c) + " GB";
        }
    }
}
