namespace Monster.Balloon.AutomataSystem
{
    /// <summary>
    /// One route of the API.
    /// 窓口の道 1 本。ゲームの側（表と裏の葉）が、世界ごとに登録で足す（芯は道の中身を知らない）。
    /// </summary>
    public interface IAgentRoute
    {
        /// <summary>
        /// The path of the route.
        /// 道（例 "/v1/back/look"）。
        /// ⚠ 窓口のスレッドもこの値を読むので、決まった字を返すこと。
        /// </summary>
        string Path { get; }

        /// <summary>
        /// The face that may call the route.
        /// 道の顔。
        /// ⚠ "/v1/back/" で始まる道は Back、ほかは Surface にすること（Host は、食い違う道を並べずに赤で知らせる）。
        /// ⚠ 窓口のスレッドもこの値を読むので、決まった値を返すこと。
        /// </summary>
        AgentFace Face { get; }

        /// <summary>
        /// The abilities this route needs; any one of them is enough.
        /// この道を使うのに要る力（どれか 1 つが開いていれば使える）。None＝いつも使える（hello だけ）。
        /// ⭐ Host は、開いた力に合わない道を並べず、門が 403 not_open で断る。
        /// ⚠ 決まった値を返すこと（Host は道を並べる時に 1 回だけ読む）。
        /// </summary>
        AgentAbilities Needs { get; }

        /// <summary>
        /// Handles one call and writes the reply.
        /// 主スレッドで呼ばれる。返事の欄を書く（断る時は <see cref="AgentReplyWriter.Fail"/>）。
        /// </summary>
        void Handle(AgentCall call, AgentReplyWriter reply);
    }
}
