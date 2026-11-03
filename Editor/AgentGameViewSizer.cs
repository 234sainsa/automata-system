using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Monster.Balloon.AutomataSystem.Editor
{
    /// <summary>
    /// Game ビューの大きさを替える係（<c>/v1/back/screen</c> の Editor の側）。
    /// <para>
    /// ⭐ 替え方は縦画面の通知の係と同じ（Unity 6.3 で通った手順）: 内部の <c>UnityEditor.GameViewSizes</c> を
    ///    リフレクションで読み、今の組（currentGroupType）に同じ寸法の「固定の解像度」があればそれを、無ければ足してから、
    ///    <c>GameView.SizeSelectionCallback(番号, null)</c> で選ぶ。
    /// ⭐ この Play で初めて替える前の番号を覚え、戻す時と Play を抜ける時にその番号へ戻す（人が選んでいた大きさを残さない）。
    ///    AI が替えた後に人が別の大きさを選んでいたら、Play を抜ける時は戻さない（人の選んだ物が先）。
    /// ⚠ 足した寸法は、組の中に残る（次からは足さずに選ぶ）。保存（SaveToHDD）はしない。
    /// </para>
    /// </summary>
    internal static class AgentGameViewSizer
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>この Play で AI が初めて替える前の番号（替えていなければ -1）。</summary>
        private static int originalIndex = -1;

        /// <summary>AI が最後に選んだ番号（Play を抜ける時に、人が選び直していないかを見る）。</summary>
        private static int lastSetIndex = -1;

        /// <summary>寸法を替える。替えられたら null。Game ビューが無ければ "no_game_view"。</summary>
        public static string TrySet(int width, int height)
        {
            EditorWindow view = FindGameView();
            if (view == null)
            {
                return "no_game_view";
            }

            object group = CurrentGroup();
            int index = FindFixed(group, width, height);
            if (index < 0)
            {
                index = AddFixed(group, width, height);
            }

            int now = SelectedIndex(view);
            if (originalIndex < 0)
            {
                originalIndex = now;
            }

            Select(view, index);
            lastSetIndex = index;
            return null;
        }

        /// <summary>この Play で替える前の番号へ戻す。戻せたら null・替えていなければ "not_changed"。</summary>
        public static string Restore()
        {
            if (originalIndex < 0)
            {
                return "not_changed";
            }

            EditorWindow view = FindGameView();
            if (view == null)
            {
                return "no_game_view";
            }

            Select(view, originalIndex);
            Forget();
            return null;
        }

        /// <summary>Play を抜ける時: AI が替えたままなら戻す（人が選び直していたら、そのまま）。</summary>
        public static void RestoreOnPlayExit()
        {
            if (originalIndex < 0)
            {
                return;
            }

            try
            {
                EditorWindow view = FindGameView();
                if (view != null && SelectedIndex(view) == lastSetIndex)
                {
                    Select(view, originalIndex);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AgentApi] Play を抜ける時に、Game ビューの大きさを戻せなかった: " + e.Message);
            }

            Forget();
        }

        private static void Forget()
        {
            originalIndex = -1;
            lastSetIndex = -1;
        }

        private static Type GameViewType => typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");

        private static EditorWindow FindGameView()
        {
            UnityEngine.Object[] views = Resources.FindObjectsOfTypeAll(GameViewType);
            return views.Length > 0 ? views[0] as EditorWindow : null;
        }

        private static object CurrentGroup()
        {
            Assembly editor = typeof(EditorWindow).Assembly;
            Type sizesType = editor.GetType("UnityEditor.GameViewSizes");
            Type singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
            object sizes = singleton.GetProperty("instance").GetValue(null);
            object groupType = sizesType.GetProperty("currentGroupType").GetValue(sizes);
            return sizesType.GetMethod("GetGroup").Invoke(sizes, new[] { groupType });
        }

        /// <summary>組の中で、同じ寸法の「固定の解像度」の番号（無ければ -1）。</summary>
        private static int FindFixed(object group, int width, int height)
        {
            Type groupType = group.GetType();
            int total = (int)groupType.GetMethod("GetTotalCount").Invoke(group, null);
            MethodInfo get = groupType.GetMethod("GetGameViewSize");
            for (int i = 0; i < total; i++)
            {
                object size = get.Invoke(group, new object[] { i });
                Type sizeType = size.GetType();
                bool isFixed = Convert.ToInt32(sizeType.GetProperty("sizeType").GetValue(size)) == 1;
                if (isFixed
                    && (int)sizeType.GetProperty("width").GetValue(size) == width
                    && (int)sizeType.GetProperty("height").GetValue(size) == height)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>「固定の解像度」の寸法を組へ足し、その番号を返す（足した物は組の終わりに並ぶ）。</summary>
        private static int AddFixed(object group, int width, int height)
        {
            Assembly editor = typeof(EditorWindow).Assembly;
            Type sizeType = editor.GetType("UnityEditor.GameViewSize");
            Type kindType = editor.GetType("UnityEditor.GameViewSizeType");
            object fixedKind = Enum.ToObject(kindType, 1);
            ConstructorInfo make = sizeType.GetConstructor(new[] { kindType, typeof(int), typeof(int), typeof(string) });
            object size = make.Invoke(new object[] { fixedKind, width, height, "AgentApi " + width + "x" + height });
            group.GetType().GetMethod("AddCustomSize").Invoke(group, new[] { size });
            return (int)group.GetType().GetMethod("GetTotalCount").Invoke(group, null) - 1;
        }

        private static int SelectedIndex(EditorWindow view) =>
            (int)GameViewType.GetProperty("selectedSizeIndex", AnyInstance).GetValue(view);

        private static void Select(EditorWindow view, int index)
        {
            GameViewType.GetMethod("SizeSelectionCallback", AnyInstance).Invoke(view, new object[] { index, null });
        }
    }
}
