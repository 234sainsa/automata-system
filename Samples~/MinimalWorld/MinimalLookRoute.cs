using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Monster.Balloon.AutomataSystem.Samples.MinimalWorld
{
    /// <summary>
    /// <c>/v1/look</c> for any Unity scene: the scene name, the camera name, and the objects inside the camera view.
    /// どのシーンでも使える表の目。シーンの名前・カメラの名前・カメラの視野に入っている物（近い順に 8 つまで）を返す。
    /// <para>
    /// ⭐ 表（Surface）の道なので、遊ぶ人が画面で分かる事だけを書く。座標・ID・本当の数値は書かない。
    ///    物の場所は、画面の左・真ん中・右と、近い・中くらい・遠いの言葉にする。
    /// ⚠ 見本なので、物の名前には GameObject の名前を使う。本物のゲームでは、画面に出ている字（名札など）にする。
    /// ⚠ 見本なので、壁の陰に隠れた物も「視野に入っている」と数える（遮る物は調べない）。
    /// </para>
    /// <para>
    /// 返事の例: <c>{"scene":"SampleScene","camera":"Main Camera","visible":[{"name":"Cube","dir":"center","dist":"near"}],"more":0}</c>
    /// （窓口が api・ok・instance・stamp・paused の欄を先に書く）。
    /// </para>
    /// </summary>
    public sealed class MinimalLookRoute : IAgentRoute
    {
        /// <summary>The most objects listed in one reply. 1 回の返事に並べる物の数の上限。</summary>
        public const int MaxVisible = 8;

        /// <summary>Distance (meters) below which an object is "near". これより近い物は near。</summary>
        public const float NearMeters = 5f;

        /// <summary>Distance (meters) below which an object is "mid". これより近い物は mid（ほかは far）。</summary>
        public const float MidMeters = 20f;

        private readonly List<(float dist, string name, string dir, string word)> seen =
            new List<(float dist, string name, string dir, string word)>();

        /// <inheritdoc />
        public string Path => "/v1/look";

        /// <inheritdoc />
        public AgentFace Face => AgentFace.Surface;

        /// <inheritdoc />
        public AgentAbilities Needs => AgentAbilities.Look;

        /// <inheritdoc />
        public void Handle(AgentCall call, AgentReplyWriter reply)
        {
            JsonWriter w = reply.Json;
            w.WritePropertyName("scene");
            w.WriteValue(SceneManager.GetActiveScene().name);

            Camera cam = Camera.main;
            w.WritePropertyName("camera");
            w.WriteValue(cam != null ? cam.name : null);

            this.Collect(cam);
            w.WritePropertyName("visible");
            w.WriteStartArray();
            int count = Mathf.Min(MaxVisible, this.seen.Count);
            for (int i = 0; i < count; i++)
            {
                w.WriteStartObject();
                w.WritePropertyName("name");
                w.WriteValue(this.seen[i].name);
                w.WritePropertyName("dir");
                w.WriteValue(this.seen[i].dir);
                w.WritePropertyName("dist");
                w.WriteValue(this.seen[i].word);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WritePropertyName("more");
            w.WriteValue(this.seen.Count - count);
        }

        /// <summary>カメラの視野に入っている物を、近い順に seen へ並べる（カメラが無ければ空）。</summary>
        private void Collect(Camera cam)
        {
            this.seen.Clear();
            if (cam == null)
            {
                return;
            }

            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
            Vector3 eye = cam.transform.position;
            foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || !GeometryUtility.TestPlanesAABB(planes, r.bounds))
                {
                    continue;
                }

                Vector3 center = r.bounds.center;
                float dist = Vector3.Distance(eye, center);
                float x = cam.WorldToViewportPoint(center).x;
                string dir = x < 1f / 3f ? "left" : x > 2f / 3f ? "right" : "center";
                string word = dist < NearMeters ? "near" : dist < MidMeters ? "mid" : "far";
                this.seen.Add((dist, r.gameObject.name, dir, word));
            }

            this.seen.Sort((a, b) => a.dist.CompareTo(b.dist));
        }
    }
}
