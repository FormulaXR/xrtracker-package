using UnityEditor;

namespace FormulaXR.Tracker.Editor
{
    static class RestartUnity
    {
        [MenuItem("Tools/Restart Unity %#u")] // Ctrl+Shift+U
        static void Restart()
        {
            EditorApplication.OpenProject(System.IO.Directory.GetCurrentDirectory());
        }
    }
}
