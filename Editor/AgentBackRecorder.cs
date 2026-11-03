using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem.Editor
{
    /// <summary>
    /// 裏の道具が使われている間だけ、Game の画面を MP4 に撮る係（人が後で見返す物・Editor の中だけ）。
    /// <para>
    /// ⭐ 撮り始めるのは、裏の道に頼みが来た時（<see cref="AgentHostSession.BackRouteCalls"/> が増えた時・試験の道具と keep_alive も入る）。表の道具だけの確かめは撮らない。
    ///    橋のログを読む道具は、ログのファイルを読むだけで、窓口に来ない＝撮り始めない。
    /// ⭐ 撮り始めた後は、表・裏どちらの頼みでも続ける。頼みが <see cref="IdleSeconds"/> 秒来ず、待っている頼みも無ければ止める。
    ///    Play を抜ける・スクリプトを読み直す・窓口が閉じる時も止める。
    /// ⭐ 1 本は最大 <see cref="SegmentSeconds"/> 秒で区切る。置き場が <see cref="MaxFolderBytes"/> を超えたら、古い物から消す。
    /// ⭐ 窓口の返事にも、ゲームのログにも何も出さない（AI がこの録画を読まない＝トークンを使わない）。出すのは、撮れなかった時の黄 1 行だけ。
    /// ⚠ Editor が止まっている間（Error Pause を含む）は絵を足さない＝動画では止まっていた間が抜ける。
    /// ⭐ 動画の時間は、撮っていた本当の時間に合わせる（ゲームが 1 秒に 15 コマより遅い時は、同じ絵を何コマか続けて足す・確かめで 1.4 倍の早回しだった）。
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    internal static class AgentBackRecorder
    {
        /// <summary>頼みが来ないまま、この秒数たったら止める。</summary>
        public const double IdleSeconds = 60;

        /// <summary>1 本の長さ（秒）。越えたら次の 1 本に替える。</summary>
        public const int SegmentSeconds = 600;

        /// <summary>置き場の上限（バイト）。越えたら古い物から消す。</summary>
        public const long MaxFolderBytes = 3L * 1024 * 1024 * 1024;

        /// <summary>フレームの終わりを待つ長さ（秒）。越えたら待ちを捨てる。</summary>
        private const double ShotWaitSeconds = 3;

        /// <summary>1 回の update で動画の時間に足す上限（秒）。Editor が長く固まった間は、動画では縮める。</summary>
        private const double MaxStepSeconds = 0.5;

        /// <summary>
        /// 置き場＝Windows の「ビデオ」の下（複数の Editor で同じ所）。
        /// ⭐ どちらの Editor が撮ったかは、ファイル名の最後（プロジェクトのフォルダ名）で分かる。上限の 3GB も 2 台で 1 つ。
        /// ⭐ 間のフォルダ名はゲームの Product Name。
        /// </summary>
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Application.productName, "AgentApi");

        /// <summary>この Editor のプロジェクトのフォルダ名（1 台の PC で Editor を 2 つ開く時の見分け）。</summary>
        private static readonly string EditorName = Path.GetFileName(Path.GetDirectoryName(Application.dataPath));

        private static AgentRecordingSegment segment;
        private static string seenPlayId;
        private static long seenBackCalls;
        private static long seenCalls;
        private static double lastActiveAt;
        private static double lastTickAt;
        private static double recordedSeconds;
        private static double nextShotAt;
        private static double shotRequestedAt;
        private static bool shotPending;
        private static bool failed;

        static AgentBackRecorder()
        {
            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting -= Stop;
            EditorApplication.quitting += Stop;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                Stop();
            }
            else if (change == PlayModeStateChange.EnteredPlayMode)
            {
                failed = false;
            }
        }

        /// <summary>
        /// ⭐ 録画の係の例外は、ここで全部受け止めて黄にする（赤にすると Error Pause が Play を止め、AI がゲームの例外と取り違える）。
        ///    ゲームの例外には触らない＝今までどおり赤で Error Pause が止め、AI は ss の wait の paused で気づく。
        /// ⚠ 録画の係は Editor の一時停止を書かない（止めるのも戻すのも、人と AI と反射の受け持ち）。
        /// </summary>
        private static void OnUpdate()
        {
            if (failed)
            {
                return;
            }

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static void Tick()
        {
            AgentHostSession session = EditorApplication.isPlaying ? AgentHost.CurrentSession : null;
            if (session == null)
            {
                Stop();
                seenPlayId = null;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (!string.Equals(session.PlayId, seenPlayId, StringComparison.Ordinal))
            {
                // 窓口が替わった（新しい Play）＝数は 0 から数え直し。
                Stop();
                seenPlayId = session.PlayId;
                seenBackCalls = 0;
                seenCalls = 0;
            }

            bool backCame = session.BackRouteCalls > seenBackCalls;
            bool anyCame = session.Calls > seenCalls || session.HeldCount > 0;
            seenBackCalls = session.BackRouteCalls;
            seenCalls = session.Calls;

            if (segment == null)
            {
                if (!backCame)
                {
                    return;
                }

                lastActiveAt = now;
                Start();
                return;
            }

            if (anyCame)
            {
                lastActiveAt = now;
            }

            if (segment.Failure != null)
            {
                Fail(segment.Failure);
                return;
            }

            if (now - lastActiveAt > IdleSeconds)
            {
                Stop();
                return;
            }

            // ⭐ 動画の時間は、止まっていない間だけ進める（止まっていた間は動画から抜ける）。
            double step = Math.Min(now - lastTickAt, MaxStepSeconds);
            lastTickAt = now;
            if (EditorApplication.isPaused)
            {
                return;
            }

            recordedSeconds += Math.Max(0, step);

            // ⚠ ここ（Editor の update）で Screen の大きさを読まない＝前にある別の窓の大きさになることがある（AgentRecordingSegment の説明・2026-11-03）。
            //    大きさの違いは、フレームの終わりに撮る係が見つけて SizeChanged で知らせる。
            if (segment.Frames >= SegmentSeconds * AgentRecordingSegment.FramesPerSecond || segment.SizeChanged)
            {
                // 長さが上限に来た・Game の画面の大きさが変わった＝次の 1 本に替える。
                Stop();
                Start();
                return;
            }

            // ⭐ フレームの終わりが 3 秒来なければ、待ちを捨てて撮り直す（来ないまま待ち続けて、録画が黙って止まらないように）。
            if ((shotPending && now - shotRequestedAt < ShotWaitSeconds) || now < nextShotAt)
            {
                return;
            }

            AgentHostBehaviour runner = session.HostObject != null ? session.HostObject.GetComponent<AgentHostBehaviour>() : null;
            if (runner == null || !runner.isActiveAndEnabled)
            {
                return;
            }

            // ⭐ 次に撮る時刻は、前の時刻に 1 コマぶん足す（遅れた時は今から数え直す＝まとめて撮らない）。
            nextShotAt = Math.Max(nextShotAt + 1.0 / AgentRecordingSegment.FramesPerSecond, now);
            shotPending = true;
            shotRequestedAt = now;
            segment.NoteRequested();
            AgentRecordingSegment target = segment;
            int slot = (int)(recordedSeconds * AgentRecordingSegment.FramesPerSecond);
            runner.AtEndOfFrame(() => Shoot(target, slot));
        }

        private static void Shoot(AgentRecordingSegment target, int slot)
        {
            shotPending = false;
            if (!ReferenceEquals(target, segment))
            {
                return;
            }

            try
            {
                target.ShootAtEndOfFrame(slot);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static void Start()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                Prune();
                string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + seenPlayId + "_" + EditorName + ".mp4";

                // ⭐ MP4 のファイルと画面の大きさは、最初の絵を撮る時（フレームの終わり）に決まる。
                segment = new AgentRecordingSegment(Path.Combine(Folder, name));
                nextShotAt = EditorApplication.timeSinceStartup;
                lastTickAt = nextShotAt;
                recordedSeconds = 0;
                shotPending = false;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static void Stop()
        {
            AgentRecordingSegment closing = segment;
            segment = null;
            shotPending = false;
            if (closing == null)
            {
                return;
            }

            try
            {
                closing.Close();
                Prune();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AgentRecorder] 録画を閉じられませんでした: " + e.Message);
            }

            // ⭐ 絵が 1 枚も無いまま閉じた＝撮れなかった訳の数を黄 1 行で出す（2026-11-03・人間用 Editor の 0 バイトの MP4 の見分け）。
            if (closing.Frames == 0 && closing.RequestedCount > 0)
            {
                Debug.LogWarning("[AgentRecorder] 絵が 1 枚も撮れないまま 1 本を閉じました: " + closing.Describe());
            }
        }

        /// <summary>撮れなかった＝黄で 1 回鳴らし、この Play では撮るのをやめる（赤にすると Error Pause が Play を止める）。</summary>
        private static void Fail(Exception e)
        {
            failed = true;
            Debug.LogWarning("[AgentRecorder] 録画をやめました（この Play ではもう撮りません）: " + e);
            Stop();
        }

        /// <summary>
        /// 置き場が上限を超えていたら、古い動画（と同じ名前の .jpg）から消す。今書いている 1 本は消さない。
        /// ⭐ もう 1 台の Editor が書いている 1 本は、開いたままなので消せない＝その 1 本は飛ばして次を消す（録画はやめない）。
        /// </summary>
        private static void Prune()
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            FileInfo[] files = new DirectoryInfo(Folder).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToArray();
            long total = files.Sum(f => f.Length);
            foreach (FileInfo file in files)
            {
                if (total <= MaxFolderBytes)
                {
                    return;
                }

                if (!string.Equals(file.Extension, ".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    long length = file.Length;
                    file.Delete();
                    total -= length;
                    FileInfo thumbnail = new FileInfo(Path.ChangeExtension(file.FullName, ".jpg"));
                    if (thumbnail.Exists)
                    {
                        length = thumbnail.Length;
                        thumbnail.Delete();
                        total -= length;
                    }
                }
                catch (IOException)
                {
                    // もう 1 台が書いている・もう 1 台が先に消した＝飛ばす。
                }
                catch (UnauthorizedAccessException)
                {
                    // 同じ（消せない 1 本は飛ばす）。
                }
            }
        }
    }
}
