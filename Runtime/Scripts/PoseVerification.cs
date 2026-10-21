using System;
using System.Text;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Evaluates a supplied pose against the current frame and returns the
	/// evidence for it, without changing anything the tracker holds.
	/// </summary>
	/// <remarks>
	/// One call renders the body's site set at the pose it is handed,
	/// searches the current image once per site, assembles the modality's normal
	/// matrix there, and reports what it found. It takes no optimization step and
	/// it advances nothing: no template is learned, no prediction store moves, no
	/// accepted or displayed pose changes, and no counter the tracking path owns
	/// is touched.
	///
	/// The native side proves that rather than asserting it. It captures every
	/// borrowed surface before the evaluation, restores them after it, and
	/// compares them field by field. A call that could not put something back
	/// fails instead of returning a number, and
	/// <see cref="FTPoseEvidence.live_state_unchanged"/> carries the outcome of
	/// that comparison into the record.
	///
	/// Two calls with the same pose against the same frame return bit-identical
	/// evidence, which is what makes the result usable for ranking candidates.
	///
	/// It gives a caller that needs to ask how good a pose would be one answer to
	/// that question, instead of one answer per caller.
	/// </remarks>
	internal static class PoseVerification
	{
		/// <summary>Floats in a row-major 4x4 pose matrix.</summary>
		private const int POSE_MATRIX_FLOATS = 16;

		/// <summary>Bytes the native evidence record occupies in this build.</summary>
		public const int POSE_EVIDENCE_SIZE = 308;

		/// <summary>Schema this package's evidence record was written against.</summary>
		public const int POSE_EVIDENCE_SCHEMA_VERSION = 1;

		/// <summary>Characters reserved for the native side's account of the call.</summary>
		private const int REPORT_CAPACITY = 512;

		/// <summary>
		/// Rewrites a geometry-frame pose into the body frame the evaluation
		/// entry points on this class take.
		/// </summary>
		/// <remarks>
		/// A host authors meshes, and reads poses back, in the geometry frame:
		/// the frame whose origin is the mesh pivot. The tracker works in the
		/// body frame, whose origin is the mesh bounding-box centre, because a
		/// body is re-centred on that centre when it is set up so that its
		/// model's viewpoint sphere is generated around it. The two frames
		/// differ by a fixed transform for the life of the body -- half the
		/// object's extent for a pivot at one end, not a rounding difference --
		/// so a pose that crosses this boundary unrewritten places the object
		/// that far from where it was meant to be, and the evidence measured
		/// there describes the wrong pose.
		///
		/// <code>cameraFromBody = cameraFromGeometry * geometry2body^-1</code>
		///
		/// A caller that wants to score the pose the tracker is currently
		/// holding does not need this at all: it reads that pose already in the
		/// body frame with <see cref="TryGetCameraFromBodyPose"/>. This exists
		/// for a pose the caller composed itself -- a perturbed start, an
		/// authored hypothesis -- which is necessarily in the geometry frame. It
		/// is safe to apply unconditionally, because the transform is the
		/// identity for a body that needed no re-centring.
		/// </remarks>
		/// <param name="bodyId">The body whose two frames are being related.</param>
		/// <param name="cameraFromGeometry16">
		/// The pose to rewrite, row-major 4x4, metres. Read and never written.
		/// </param>
		/// <param name="cameraFromBody16">
		/// Receives the rewritten pose, row-major 4x4. Valid only when this
		/// returns true.
		/// </param>
		/// <returns>
		/// True when the body's transform could be read and the pose rewritten.
		/// </returns>
		public static bool TryToBodyFrame(string bodyId,
			float[] cameraFromGeometry16, out float[] cameraFromBody16)
		{
			cameraFromBody16 = null;
			if (cameraFromGeometry16 == null ||
				cameraFromGeometry16.Length != POSE_MATRIX_FLOATS)
			{
				return false;
			}
			if (!TryGetGeometryToBodyPose(bodyId, out float[] geometryToBody16))
				return false;
			cameraFromBody16 = Multiply(cameraFromGeometry16,
				InvertRigid(geometryToBody16));
			return cameraFromBody16 != null;
		}

		/// <summary>
		/// Rewrites a body-frame pose back into the geometry frame a host reads
		/// poses in. The inverse of <see cref="TryToBodyFrame"/>.
		/// </summary>
		/// <param name="bodyId">The body whose two frames are being related.</param>
		/// <param name="cameraFromBody16">
		/// The pose to rewrite, row-major 4x4, metres. Read and never written.
		/// </param>
		/// <param name="cameraFromGeometry16">
		/// Receives the rewritten pose, row-major 4x4. Valid only when this
		/// returns true.
		/// </param>
		/// <returns>
		/// True when the body's transform could be read and the pose rewritten.
		/// </returns>
		public static bool TryToGeometryFrame(string bodyId,
			float[] cameraFromBody16, out float[] cameraFromGeometry16)
		{
			cameraFromGeometry16 = null;
			if (cameraFromBody16 == null ||
				cameraFromBody16.Length != POSE_MATRIX_FLOATS)
			{
				return false;
			}
			if (!TryGetGeometryToBodyPose(bodyId, out float[] geometryToBody16))
				return false;
			cameraFromGeometry16 = Multiply(cameraFromBody16, geometryToBody16);
			return cameraFromGeometry16 != null;
		}

		/// <summary>
		/// Reads the pose the tracker currently holds for one body, already in
		/// the body frame the evaluation entry points take.
		/// </summary>
		/// <param name="bodyId">The tracked body to read.</param>
		/// <param name="cameraFromBody16">
		/// Receives the pose, row-major 4x4. Valid only when this returns true.
		/// </param>
		/// <returns>True when the tracker had a pose to report.</returns>
		public static bool TryGetCameraFromBodyPose(string bodyId,
			out float[] cameraFromBody16)
		{
			cameraFromBody16 = null;
			if (string.IsNullOrEmpty(bodyId)) return false;
			var pose = new float[POSE_MATRIX_FLOATS];
			bool read;
			try
			{
				read = FTBridge.FT_GetCameraFromBodyPose(bodyId, pose);
			}
			catch (DllNotFoundException)
			{
				return false;
			}
			catch (EntryPointNotFoundException)
			{
				return false;
			}
			if (!read || !IsFinite(pose)) return false;
			cameraFromBody16 = pose;
			return true;
		}

		/// <summary>
		/// Reads one body's geometry-to-body transform, row-major 4x4, metres.
		/// It is the identity for a body that needed no re-centring at set-up.
		/// </summary>
		/// <param name="bodyId">The body whose transform is wanted.</param>
		/// <param name="geometryToBody16">
		/// Receives the transform, row-major 4x4. Valid only when this returns
		/// true.
		/// </param>
		/// <returns>True when the transform could be read.</returns>
		public static bool TryGetGeometryToBodyPose(string bodyId,
			out float[] geometryToBody16)
		{
			geometryToBody16 = null;
			if (string.IsNullOrEmpty(bodyId)) return false;
			var transform = new float[POSE_MATRIX_FLOATS];
			bool read;
			try
			{
				read = FTBridge.FT_GetBodyGeometryToBodyPose(bodyId, transform,
					POSE_MATRIX_FLOATS);
			}
			catch (DllNotFoundException)
			{
				return false;
			}
			catch (EntryPointNotFoundException)
			{
				return false;
			}
			if (!read || !IsFinite(transform)) return false;
			geometryToBody16 = transform;
			return true;
		}

		/// <summary>Row-major 4x4 product, left times right.</summary>
		/// <param name="left">The left operand, 16 floats row-major.</param>
		/// <param name="right">The right operand, 16 floats row-major.</param>
		/// <returns>The product, or null when either operand is not 16 floats.</returns>
		public static float[] Multiply(float[] left, float[] right)
		{
			if (left == null || left.Length != POSE_MATRIX_FLOATS ||
				right == null || right.Length != POSE_MATRIX_FLOATS)
			{
				return null;
			}
			var product = new float[POSE_MATRIX_FLOATS];
			for (int row = 0; row < 4; ++row)
			{
				for (int column = 0; column < 4; ++column)
				{
					float sum = 0.0f;
					for (int inner = 0; inner < 4; ++inner)
						sum += left[row * 4 + inner] * right[inner * 4 + column];
					product[row * 4 + column] = sum;
				}
			}
			return product;
		}

		/// <summary>
		/// Inverse of a rigid row-major 4x4: the rotation transposed, and the
		/// translation rotated back through it and negated.
		/// </summary>
		/// <remarks>
		/// Every transform this class inverts is rigid by construction -- a
		/// body's centring transform and a camera-relative object pose carry no
		/// scale and no shear -- so transposing the rotation is exact where a
		/// general inverse would spend arithmetic and lose bits.
		/// </remarks>
		/// <param name="pose16">The transform to invert, 16 floats row-major.</param>
		/// <returns>The inverse, or null when the input is not 16 floats.</returns>
		public static float[] InvertRigid(float[] pose16)
		{
			if (pose16 == null || pose16.Length != POSE_MATRIX_FLOATS) return null;
			var inverse = new float[POSE_MATRIX_FLOATS];
			for (int row = 0; row < 3; ++row)
			{
				for (int column = 0; column < 3; ++column)
					inverse[row * 4 + column] = pose16[column * 4 + row];
			}
			for (int row = 0; row < 3; ++row)
			{
				inverse[row * 4 + 3] =
					-(inverse[row * 4 + 0] * pose16[3] +
						inverse[row * 4 + 1] * pose16[7] +
						inverse[row * 4 + 2] * pose16[11]);
			}
			inverse[12] = 0.0f;
			inverse[13] = 0.0f;
			inverse[14] = 0.0f;
			inverse[15] = 1.0f;
			return inverse;
		}

		private static bool IsFinite(float[] values)
		{
			for (int index = 0; index < values.Length; ++index)
			{
				if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
					return false;
			}
			return true;
		}

		/// <summary>
		/// Evaluates <paramref name="cameraFromBody16"/> against the current frame
		/// for one body.
		/// </summary>
		/// <param name="bodyId">The tracked body to evaluate against.</param>
		/// <param name="cameraFromBody16">
		/// The pose to evaluate, row-major 4x4, metres, in the native convention.
		/// It is read and never written.
		/// </param>
		/// <param name="evidence">The record, valid only when this returns true.</param>
		/// <param name="report">
		/// The native side's account of the call, populated on success and on
		/// failure alike. On failure it names what went wrong.
		/// </param>
		/// <returns>
		/// True when the evaluation completed and left every borrowed surface
		/// unchanged. False otherwise, with the reason in
		/// <paramref name="report"/>.
		/// </returns>
		public static bool TryEvaluate(string bodyId, float[] cameraFromBody16,
			out FTPoseEvidence evidence, out string report)
		{
			evidence = default;
			if (string.IsNullOrEmpty(bodyId))
			{
				report = "no body was named";
				return false;
			}
			if (cameraFromBody16 == null || cameraFromBody16.Length != POSE_MATRIX_FLOATS)
			{
				report = "the pose must be " + POSE_MATRIX_FLOATS + " floats, row-major";
				return false;
			}

			StringBuilder text = new StringBuilder(REPORT_CAPACITY);
			int status;
			try
			{
				// The evaluation moves the live camera and body for a moment and renders with the shared geometry.
				XRTrackerManager.WaitForTrackingStepToReturn();
				status = FTBridge.FT_EvaluatePoseEvidence(bodyId, cameraFromBody16,
					cameraFromBody16.Length, ref evidence, POSE_EVIDENCE_SIZE,
					text, REPORT_CAPACITY);
			}
			catch (DllNotFoundException exception)
			{
				report = "the native library is not loaded: " + exception.Message;
				return false;
			}
			catch (EntryPointNotFoundException exception)
			{
				report = "this build of the native library has no evaluation entry point: "
					+ exception.Message;
				return false;
			}

			report = text.ToString();
			// The native side already refuses to report evidence it could not
			// prove neutral, so this is a second reading of the same fact rather
			// than the only one. It is checked here as well because a caller that
			// reads the record without reading the status code is the failure this
			// guards, and it costs one comparison.
			return status == FTErrorCode.OK && evidence.live_state_unchanged == 1;
		}
	}
}
