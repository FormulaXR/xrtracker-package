using UnityEditor;
using UnityEngine;

namespace IV.FormulaTracker.Editor
{
	static class TrackerMenuItems
	{
		[MenuItem("GameObject/XRTracker/AR Tracker", false, 10)]
		static void CreateARTracker(MenuCommand menuCommand)
		{
			InstantiatePrefab("AR_Tracker", menuCommand);
		}

		[MenuItem("GameObject/XRTracker/PC Tracker", false, 11)]
		static void CreatePCTracker(MenuCommand menuCommand)
		{
			InstantiatePrefab("PC_Tracker", menuCommand);
		}

		static void InstantiatePrefab(string name, MenuCommand menuCommand)
		{
			var prefab = Resources.Load<GameObject>(name);
			if (prefab == null)
			{
				Debug.LogError($"[XRTracker] Prefab '{name}' not found in Resources.");
				return;
			}

			var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
			if (instance == null) return;

			instance.name = prefab.name;

			GameObjectUtility.SetParentAndAlign(instance, menuCommand.context as GameObject);
			Undo.RegisterCreatedObjectUndo(instance, $"Create {instance.name}");
			Selection.activeGameObject = instance;
		}
	}
}
