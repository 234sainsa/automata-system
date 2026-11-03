using UnityEngine;

namespace Monster.Balloon.AutomataSystem.Samples.MinimalWorld
{
    /// <summary>
    /// Hands the minimal world to the Agent API host while this component is enabled.
    /// この部品が有効な間、いちばん小さい世界（<see cref="MinimalAgentWorld"/>）を窓口に渡す。
    /// <para>
    /// ⭐ 有効になった時に <see cref="AgentHost.AttachWorld"/>（世界ができた＝道が並ぶ）、
    ///    無効になった時に <see cref="AgentHost.DetachWorld"/>（世界を畳んだ＝道が外れる）を呼ぶ。
    /// ⭐ シーンにこの部品が無ければ、Play の頭（シーンを読んだ後）に、この見本が 1 つ作る。
    ///    だから、見本を Import しただけで、どのシーンでも hello と look が通る。
    ///    自分の世界を作ったら、この見本のフォルダは消してよい。
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MinimalAgentWorldBinder : MonoBehaviour
    {
        private MinimalAgentWorld world;

        private void OnEnable()
        {
            if (this.world == null)
            {
                this.world = new MinimalAgentWorld();
            }

            AgentHost.AttachWorld(this.world);
        }

        private void OnDisable()
        {
            if (this.world != null)
            {
                AgentHost.DetachWorld(this.world);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateIfMissing()
        {
            if (Object.FindAnyObjectByType<MinimalAgentWorldBinder>() != null)
            {
                return;
            }

            var go = new GameObject("Agent API Minimal World");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<MinimalAgentWorldBinder>();
        }
    }
}
