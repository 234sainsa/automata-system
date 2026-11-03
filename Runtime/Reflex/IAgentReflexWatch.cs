using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// The watch of reflexes: stop guards and moving reflexes. One per world.
    /// 反射の見張り（止める守り・動く反射）。世界ごとに 1 つ、ゲームの側の葉が DI に結ぶ。
    /// <para>
    /// ⭐ 見張りは、AI が腕を掛けている間（<c>POST /v1/reflex/arm</c>）だけ動く。腕が無い間は、購読も見張りもしない。
    /// ⭐ 見張りは、世界を守る係なので裏の値（HUD が隠れていても本当の HP）を読む。
    ///    ただし、表へ出すのは守りと反射の名前と理由の字だけ（値は <see cref="IAgentReflexContext.Trip"/> などの裏の中身にだけ入れる）。
    /// ⭐ 動く反射（逃げる等）は、<see cref="IAgentReflexContext.Act"/> で手足の命令を走らせる（ゲームの判定は手足と同じ）。
    /// ⚠ どの口も主スレッドで呼ばれる。<see cref="Tick"/> は、Editor が止まっている間は呼ばれない。
    /// ⚠ <see cref="Tick"/> が例外を投げると、Host はこの見張りをやめ、赤いログの守り（red_log）として止める（守りが減ったことを AI に知らせる）。
    ///   腕を掛け直すと、Host は見張りを始め直す。
    /// </para>
    /// </summary>
    public interface IAgentReflexWatch
    {
        /// <summary>
        /// The names of the stop guards.
        /// この見張りが見る守りの名前（hello に並べる・例 "hp_low"）。⚠ 決まった値を返すこと。
        /// </summary>
        IReadOnlyList<string> Guards { get; }

        /// <summary>
        /// The names of the moving reflexes.
        /// この見張りが持つ動く反射の名前（hello に並べ・arm の modes で選ぶ・例 "flee"）。無ければ空。⚠ 決まった値を返すこと。
        /// </summary>
        IReadOnlyList<string> Modes { get; }

        /// <summary>
        /// Starts the watch for one Play.
        /// 見張りを始める（腕を掛けた時・腕の間に世界ができた時）。今の姿を基準にする。
        /// ⭐ 何度呼ばれてもよい（呼ばれるたびに基準を取り直す）。
        /// </summary>
        void Begin(IAgentReflexContext context);

        /// <summary>
        /// Called every frame while the watch is on.
        /// 毎フレーム（ゲームが動いている間だけ）。守りが働いたら <see cref="IAgentReflexContext.Trip"/> を呼ぶ。
        /// </summary>
        void Tick(IAgentReflexContext context);

        /// <summary>
        /// Ends the watch.
        /// 見張りをやめる（腕を外した時・世界を畳む時・窓口を閉じる時）。何度呼んでもよい。
        /// </summary>
        void End();
    }

    /// <summary>
    /// What the watch can ask the host while it runs.
    /// 見張りが、守りの働きを Host へ知らせる口と、AI が腕に付けた決まり。
    /// </summary>
    public interface IAgentReflexContext
    {
        /// <summary>
        /// The time of day (0-1439 minutes) before which the run must stop; -1 when not set.
        /// 走行の「この時刻より前に止める」（その日の 0〜1439 分）。決めていなければ -1。
        /// </summary>
        int StopBeforeMinute { get; }

        /// <summary>
        /// Trips a guard; stop says whether the Editor should pause.
        /// 守りが働いた。Host は合図 reflex.tripped を書き、掛け金を掛け（止めない守りも掛ける）、<paramref name="stop"/> なら Editor を止める。
        /// <para>
        /// ⭐ 表の帳面には、守りの名前と止めたかだけが載る。<paramref name="backData"/> は裏の帳面にだけ載る（本当の HP など）。
        /// ⚠ 同じ出来事で何度も呼ばないこと（見張りの側で、1 回働いたら元に戻るまで黙る）。
        /// ⭐ AI が腕の ignore で選んだ守りなら、Host は合図 reflex.ignored だけを書く（掛け金も一時停止も無い＝見張りの側は同じに呼べばよい）。
        /// </para>
        /// </summary>
        /// <param name="guard">守りの名前（<see cref="IAgentReflexWatch.Guards"/> のどれか）。</param>
        /// <param name="stop">止めるか。false は「もう遅い」の知らせだけ（例 gameover）。</param>
        /// <param name="backData">裏の帳面だけに載せる中身（無ければ null）。</param>
        void Trip(string guard, bool stop, JObject backData = null);

        /// <summary>
        /// Returns true when the moving reflex is switched on.
        /// 動く反射 <paramref name="mode"/> を、AI が腕で選んでいるか（arm の modes・選ばなければ見張りの全部）。
        /// </summary>
        bool IsModeOn(string mode);

        /// <summary>
        /// True while a moving-reflex command (started with Act) is running.
        /// 動く反射の命令が走っているか（<see cref="Act"/> で始めた物）。
        /// </summary>
        bool IsActing { get; }

        /// <summary>
        /// True while a command of the AI is running.
        /// AI の命令が走っているか（動く反射の命令は数えない）。
        /// ⭐ 急がない反射（食べる）は、これが true の間は始めない（AI が逃げて歩いている命令を、食べるために止めない）。
        /// </summary>
        bool IsAgentActing { get; }

        /// <summary>
        /// Runs a moving reflex command; returns null or the reason it did not run.
        /// 動く反射が動く。Host は合図 reflex.acted を書き、AI の命令を interrupted（by reflex.acted）で終え、
        /// 手足（AI の借り・無ければ反射が借りる）で <paramref name="command"/> を走らせる。
        /// <para>
        /// 走らせたら null。走らせなかった時は理由: off（腕で選ばれていない）・acting（別の反射が動いている）・latched（掛け金の間）・
        /// paused（止まっている間）・human_active（人が触った直後）・no_hands・loop（同じ反射が 1 分に 3 回＝止める守り reflex_loop を働かせた）。
        /// ⭐ 表の合図には、反射の名前・理由の字・命令の番号だけ。<paramref name="backData"/> は裏の帳面にだけ載る。
        /// </para>
        /// </summary>
        /// <param name="mode">動く反射の名前（<see cref="IAgentReflexWatch.Modes"/> のどれか）。</param>
        /// <param name="why">動いた理由の短い字（値は入れない・例 "hurt"）。</param>
        string Act(string mode, string why, IAgentCommand command, JObject args, JObject backData = null);

        /// <summary>
        /// Notes that a reflex acted by itself; returns false when it must not.
        /// 命令の中で動いた反射（unstuck＝歩く命令を止めずに、その中で跳ぶ・ずれる）を、合図 reflex.acted に書く。
        /// 同じ反射が 1 分に 3 回なら、書かずに止める守り reflex_loop を働かせて false を返す。腕で選ばれていなければ false。
        /// </summary>
        bool NoteActed(string mode, string why, JObject backData = null);
    }
}
