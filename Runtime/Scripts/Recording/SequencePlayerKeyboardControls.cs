#if ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// Keyboard controls for SequencePlayerFeeder using the legacy Input Manager.
	/// Attach to the same GameObject as SequencePlayerFeeder.
	/// Requires "Active Input Handling" set to "Input Manager (Old)" or "Both" in Player Settings.
	/// </summary>
	[RequireComponent(typeof(SequencePlayerFeeder))]
	public class SequencePlayerKeyboardControls : MonoBehaviour
	{
#if UNITY_EDITOR
		[SerializeField] private KeyCode _pauseKey = KeyCode.Space;
		[SerializeField] private KeyCode _stepForwardKey = KeyCode.RightArrow;
		[SerializeField] private KeyCode _stepBackwardKey = KeyCode.LeftArrow;
		[SerializeField] private KeyCode _rewindKey = KeyCode.Home;

		private SequencePlayerFeeder _feeder;

		private void Awake()
		{
			_feeder = GetComponent<SequencePlayerFeeder>();
		}

		private void Update()
		{
			if (_feeder == null || _feeder.FrameCount == 0) return;

			if (Input.GetKeyDown(_pauseKey))
			{
				if (_feeder.IsPlaying) _feeder.Pause();
				else _feeder.Play();
			}

			if (Input.GetKeyDown(_stepForwardKey))
				_feeder.Step(1);

			if (Input.GetKeyDown(_stepBackwardKey))
				_feeder.Step(-1);

			if (Input.GetKeyDown(_rewindKey))
				_feeder.Seek(0);
		}
#endif
	}
}
#endif
