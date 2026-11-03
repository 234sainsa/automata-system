using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// 撮る係。Play 1 回ぶん（窓口の持ち物）。
    /// <para>
    /// ⭐ 頼まれた時だけ、1 秒に 1 枚まで撮る。撮るのはフレームの終わり（描き終わった後）で、
    ///    同じフレームの中で世界の口（<see cref="IAgentCaptureEyes"/>）に look を書かせる＝絵と look が同じフレーム。
    /// ⭐ 絵は置き場に「ID.png」で書き、新しい <see cref="KeepFiles"/> 枚だけを残す。置き場は係が決める（頼みからは決めさせない）＝
    ///    Editor はプロジェクトの <c>Logs/AgentApi/captures</c>（git に載らない）・ビルドは persistentDataPath の下。
    /// ⭐ 主スレッドは絵の色を写すだけ。PNG にしてファイルへ書くのは別のスレッド（<c>EncodeArrayToPNG</c> はスレッドを選ばない）。
    /// ⭐ フレームの終わりを待つのは 1 本だけ（来ないまま次の頼みが来ても、待ちを積まない＝来た時にまとめて何枚も撮らない）。
    ///    窓口が待ちきれずに答えた控えは撮らない。頼んだ後に世界が作り直されたら、撮らずに world_changed で答える（古い世界の口を使わない）。
    /// ⚠ 止まっている Editor ではフレームの終わりが来ない＝道は止まっている間は断る。
    ///   それでも来なければ <see cref="FrameWaitMs"/> で no_frame として答える（Game の画面が描かれていない時も同じ）。
    /// </para>
    /// </summary>
    internal sealed class AgentCaptureDesk
    {
        /// <summary>撮る間の短さ（1 秒に 1 枚まで）。</summary>
        public const int GapMs = 1000;

        /// <summary>置き場に残す枚数（古い物から消す）。</summary>
        public const int KeepFiles = 200;

        /// <summary>フレームの終わりを待つ長さ。越えたら no_frame で答える。</summary>
        public const int FrameWaitMs = 3000;

        /// <summary>絵をファイルにし終えるまで待つ長さ（頼みを受けてから）。</summary>
        public const int WriteWaitMs = 8000;

        /// <summary>撮れなかった（主スレッドの例外）。赤で 1 回鳴らす。</summary>
        public const string CaptureFailed = "capture_failed";

        /// <summary>絵をファイルに書けなかった（置き場の都合）。黄で 1 回鳴らす。</summary>
        public const string WriteFailed = "write_failed";

        /// <summary>頼んだ後に世界が作り直された（撮らなかった）。鳴らさない。</summary>
        public const string WorldChanged = "world_changed";

        /// <summary>書きかけ（.part）を、古いと見なして消すまでの長さ。</summary>
        private static readonly TimeSpan PartMaxAge = TimeSpan.FromMinutes(1);

        private readonly string playId;
        private readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);
        private int count;
        private long lastStartMs = long.MinValue / 2;
        private AgentCaptureTicket running;
        private volatile bool closed;

        // フレームの終わりの待ち（1 本だけ）。waitGen が違う待ちは、来ても何もしない。
        private AgentCaptureTicket waiting;
        private IAgentCaptureEyes waitingEyes;
        private int waitGen;
        private bool waitPending;
        private long waitStartedMs = long.MinValue / 2;

        public AgentCaptureDesk(string playId)
        {
            this.playId = playId;
            this.Folder = DefaultFolder();
        }

        /// <summary>絵の置き場（絶対パス）。</summary>
        public string Folder { get; }

        /// <summary>この Play で撮り始めた枚数（撮った絵の ID の番号＝<c>&lt;playId&gt;-&lt;番号&gt;</c> の最後の番号・主スレッドで読む）。</summary>
        public int Count => this.count;

        /// <summary>まだ答えが出ず、待つ長さも越えていない撮影があるか。</summary>
        public bool IsBusy(long nowMs) => this.running != null && !this.running.Finished && !IsOverdue(this.running, nowMs);

        /// <summary>控えが、待つ長さを越えた（フレームの終わりが来ない・ファイルにし終わらない）。</summary>
        public static bool IsOverdue(AgentCaptureTicket ticket, long nowMs)
        {
            if (ticket.Finished)
            {
                return false;
            }

            long waited = nowMs - ticket.StartedMs;
            return (!ticket.Shot && waited > FrameWaitMs) || waited > WriteWaitMs;
        }

        /// <summary>
        /// 撮り始める（主スレッド・そのフレームの PostLateUpdate）。断る時は理由の字（busy・too_soon・no_host）を返す。
        /// ⭐ 開発用の帯を隠すのは、このフレームの描く前（世界の口の <see cref="IAgentCaptureEyes.BeforeFrame"/>）。
        ///    そこで落ちたら、控えを capture_failed にして返す（窓口はすぐ答える）。
        /// </summary>
        public string TryStart(JObject request, bool hideOverlays, IAgentCaptureEyes eyes, GameObject host, long nowMs, out AgentCaptureTicket ticket)
        {
            ticket = null;
            if (this.closed)
            {
                return "no_host";
            }

            if (this.IsBusy(nowMs))
            {
                return "busy";
            }

            if (nowMs - this.lastStartMs < GapMs)
            {
                return "too_soon";
            }

            AgentHostBehaviour runner = host != null ? host.GetComponent<AgentHostBehaviour>() : null;
            if (runner == null || !runner.isActiveAndEnabled)
            {
                return "no_host";
            }

            this.count++;
            this.lastStartMs = nowMs;
            var made = new AgentCaptureTicket(this.playId + "-" + this.count, request, hideOverlays, Time.frameCount, nowMs, AgentHost.Epoch);
            this.running = made;
            ticket = made;
            try
            {
                eyes.BeforeFrame(hideOverlays, made.Hidden);
            }
            catch (Exception e)
            {
                made.Fail(CaptureFailed, e.GetType().Name, e.ToString());
                return null;
            }

            this.waiting = made;
            this.waitingEyes = eyes;
            if (!this.waitPending || nowMs - this.waitStartedMs > FrameWaitMs)
            {
                // ⭐ 待ちが無い時だけ、新しく待つ。前の待ちが 3 秒来ないまま（Host が止まった等）なら、待ちを替える（古い待ちは番号が違えば何もしない）。
                int gen = ++this.waitGen;
                this.waitPending = true;
                this.waitStartedMs = nowMs;
                runner.AtEndOfFrame(() => this.OnEndOfFrame(gen));
            }

            return null;
        }

        /// <summary>窓口を閉じた（走っている撮影の続きは捨てる）。</summary>
        public void Close()
        {
            this.closed = true;
            this.waiting = null;
            this.waitingEyes = null;
        }

        /// <summary>同じ知らせを黄で 1 回だけ鳴らす（主スレッド・Play 1 回ぶん）。</summary>
        public void WarnOnce(string key, string message)
        {
            if (this.warned.Add(key))
            {
                Debug.LogWarning(message);
            }
        }

        /// <summary>フレームの終わり（主スレッド）。今の待ちなら、待っている控えを撮る。</summary>
        private void OnEndOfFrame(int gen)
        {
            if (gen != this.waitGen)
            {
                return;
            }

            this.waitPending = false;
            AgentCaptureTicket ticket = this.waiting;
            IAgentCaptureEyes eyes = this.waitingEyes;
            this.waiting = null;
            this.waitingEyes = null;
            if (ticket != null)
            {
                this.Shoot(ticket, eyes);
            }
        }

        /// <summary>フレームの終わりに撮る（主スレッド）。</summary>
        private void Shoot(AgentCaptureTicket ticket, IAgentCaptureEyes eyes)
        {
            if (this.closed || ticket.Finished || ticket.Abandoned)
            {
                return;
            }

            if (ticket.Epoch != AgentHost.Epoch)
            {
                ticket.Fail(WorldChanged, "look_again", null);
                return;
            }

            Texture2D picture = null;
            RenderTexture before = RenderTexture.active;
            try
            {
                // ⭐ 撮る前に、描く先（RenderTexture.active）を画面へ戻す。
                //    同じフレームの終わりに先に動いた係（録画の Blit など）が描く先を自分の RenderTexture にしたままだと、
                //    この口は画面ではなく真っ黒な絵（全部 0）を返す（測った結果＝人の見る Game ビューは正しいのに、撮る道具の絵だけ明るさ 0.0）。
                RenderTexture.active = null;
                picture = ScreenCapture.CaptureScreenshotAsTexture();
                int width = picture.width;
                int height = picture.height;
                Color32[] colors = picture.GetPixels32();

                // ⭐ 絵の入れ物はすぐ消す（色は写し終えた・Destroy だと次のフレームまで残る＝4K で 33MB）。
                Object.DestroyImmediate(picture);
                picture = null;

                var body = new JTokenWriter();
                body.WriteStartObject();
                eyes.Describe(body, ticket.Request, new CapturePixels(colors, width, height));
                body.WriteEndObject();
                ticket.NoteShot(Time.frameCount, width, height, body.Token as JObject);

                string folder = this.Folder;
                Task.Run(() => WriteFile(ticket, colors, width, height, folder));
            }
            catch (Exception e)
            {
                ticket.Fail(CaptureFailed, e.GetType().Name, e.ToString());
            }
            finally
            {
                RenderTexture.active = before;
                if (picture != null)
                {
                    Object.DestroyImmediate(picture);
                }
            }
        }

        /// <summary>絵を PNG にして書き、古い絵を消す（別のスレッド）。⚠ Unity の物（Texture など）に触らない。</summary>
        private static void WriteFile(AgentCaptureTicket ticket, Color32[] colors, int width, int height, string folder)
        {
            string part = null;
            try
            {
                if (ticket.Abandoned)
                {
                    return;
                }

                // ⭐ 透けを残さない（カメラの消し色の a が 0 だと、絵を開く道具によっては透けて見える）。
                //    色の並びをそのまま PNG にする（別の byte[] を作らない＝4K で 33MB を倍にしない）。
                for (int i = 0; i < colors.Length; i++)
                {
                    colors[i].a = 255;
                }

                byte[] png = ImageConversion.EncodeArrayToPNG(
                    colors, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height, (uint)(width * 4));

                // ⭐ 書きかけを読まれないよう、別の名前で書いてから付け替える。
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, ticket.Id + ".png");
                part = file + ".part";
                File.WriteAllBytes(part, png);
                File.Delete(file);
                File.Move(part, file);
                part = null;

                // ⭐ 古い絵を消す所で落ちても、書けた絵は書けたと答える（失敗は控えに書き、窓口が黄で 1 回鳴らす）。
                try
                {
                    Prune(folder, KeepFiles, file);
                }
                catch (Exception e)
                {
                    ticket.NotePruneFailed(e.GetType().Name + ": " + e.Message);
                }

                ticket.Succeed(file.Replace('\\', '/'));
            }
            catch (Exception e)
            {
                TryDelete(part);
                ticket.Fail(WriteFailed, e.GetType().Name, e.Message);
            }
        }

        /// <summary>
        /// 置き場の絵を、新しい <paramref name="keep"/> 枚だけにする（古い物から消す・消せない物は残す）。
        /// ⭐ 今書いた絵（<paramref name="justWritten"/>）は消さない（PC の時計が戻ると、今の絵がいちばん古く見える）。古い書きかけ（.part）も消す。
        /// </summary>
        private static void Prune(string folder, int keep, string justWritten)
        {
            var dir = new DirectoryInfo(folder);
            DateTime partLimit = DateTime.UtcNow - PartMaxAge;
            foreach (FileInfo part in dir.GetFiles("*.part"))
            {
                if (part.LastWriteTimeUtc < partLimit)
                {
                    TryDelete(part.FullName);
                }
            }

            FileInfo[] files = dir.GetFiles("*.png");
            if (files.Length <= keep)
            {
                return;
            }

            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            string keepPath = Path.GetFullPath(justWritten);
            int over = files.Length - keep;
            for (int i = 0; i < files.Length && over > 0; i++)
            {
                if (string.Equals(files[i].FullName, keepPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryDelete(files[i].FullName))
                {
                    over--;
                }
            }
        }

        /// <summary>ファイルを消す（無い・開いている人がいる・消せない時は残す）。消せたら true。</summary>
        private static bool TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                // 開いている人がいる物は、次の回に消す。
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // 消せない物は残す（置き場は係のフォルダだけ）。
                return false;
            }
        }

        private static string DefaultFolder()
        {
#if UNITY_EDITOR
            // Editor はプロジェクトの Logs の下（git に載らない＝.gitignore の Logs/）。dataPath はプロジェクトの Assets。
            return Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Logs", "AgentApi", "captures");
#else
            return Path.Combine(Application.persistentDataPath, "AgentApi", "captures");
#endif
        }

        /// <summary>撮った絵の色（下の行から並ぶ・Texture2D と同じ）を測る口。</summary>
        private sealed class CapturePixels : IAgentCapturePixels
        {
            /// <summary>1 つの四角で拾う点の数の目安（大きな四角は間を空けて拾う）。</summary>
            private const int MaxSamples = 40000;

            private readonly Color32[] colors;

            public CapturePixels(Color32[] colors, int width, int height)
            {
                this.colors = colors;
                this.Width = width;
                this.Height = height;
            }

            public int Width { get; }

            public int Height { get; }

            public float? MeanLight(Rect area)
            {
                int x0 = Mathf.Clamp(Mathf.FloorToInt(area.xMin * this.Width), 0, this.Width);
                int x1 = Mathf.Clamp(Mathf.CeilToInt(area.xMax * this.Width), 0, this.Width);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(area.yMin * this.Height), 0, this.Height);
                int y1 = Mathf.Clamp(Mathf.CeilToInt(area.yMax * this.Height), 0, this.Height);
                if (x1 <= x0 || y1 <= y0 || this.colors.Length < this.Width * this.Height)
                {
                    return null;
                }

                long pixels = (long)(x1 - x0) * (y1 - y0);
                int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(pixels / (double)MaxSamples)));
                double sum = 0;
                long n = 0;
                for (int y = y0; y < y1; y += step)
                {
                    int row = y * this.Width;
                    for (int x = x0; x < x1; x += step)
                    {
                        Color32 c = this.colors[row + x];
                        sum += (0.2126 * c.r) + (0.7152 * c.g) + (0.0722 * c.b);
                        n++;
                    }
                }

                return n > 0 ? (float)(sum / (n * 255.0)) : (float?)null;
            }
        }
    }
}
