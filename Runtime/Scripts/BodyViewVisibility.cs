using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// What the current camera pose says about whether a body should be visible.
	/// </summary>
	/// <remarks>
	/// This is a statement about geometry, not about evidence. It answers "given
	/// where we believe the body is and where the camera is now, should any of it
	/// land in the image?" — a question that has an answer even on a frame where
	/// the tracker found nothing at all. It is kept apart from the quality score on
	/// purpose, because a body that is out of frame and a body that is in frame and
	/// unrecognised are different situations, and a score alone cannot tell them
	/// apart.
	/// </remarks>
	internal readonly struct BodyViewVisibility
	{
		internal BodyViewVisibility(bool measured, float inViewFraction, bool couldBeInView,
			bool shouldBeVisible, float centreDepthMeters, float boundsDiagonalMeters)
		{
			Measured = measured;
			InViewFraction = inViewFraction;
			CouldBeInView = couldBeInView;
			ShouldBeVisible = shouldBeVisible;
			CentreDepthMeters = centreDepthMeters;
			BoundsDiagonalMeters = boundsDiagonalMeters;
		}

		/// <summary>
		/// Whether the predicate could be evaluated at all this frame. False when
		/// the camera pose, the intrinsics or the body's own extent were missing.
		/// </summary>
		public bool Measured { get; }

		/// <summary>
		/// The share of the body's bounding volume corners that project inside the
		/// image with positive depth, in <c>[0,1]</c>.
		/// </summary>
		/// <remarks>
		/// A bounding volume over-states extent, so this over-states visibility: a
		/// body whose bounds clip the image edge reports a positive fraction even
		/// when no part of the body itself is in frame. That direction is the safe
		/// one — it makes "should not be visible" the harder claim to make.
		/// </remarks>
		public float InViewFraction { get; }

		/// <summary>
		/// Whether any part of the body's bounding volume could be inside the view.
		/// One-sided: false only when the body is provably outside it.
		/// </summary>
		public bool CouldBeInView { get; }

		/// <summary>
		/// Whether the body should be visible. True whenever
		/// <see cref="CouldBeInView"/> is true; the configured margin can only make
		/// it true in more cases, never fewer.
		/// </summary>
		public bool ShouldBeVisible { get; }

		/// <summary>
		/// How far in front of the camera the body's centre sits, in metres.
		/// Negative when it is behind the camera.
		/// </summary>
		public float CentreDepthMeters { get; }

		/// <summary>
		/// The diagonal of the body's bounding volume, in metres. Recorded because
		/// an in-view fraction is only interpretable against the extent it measures.
		/// </summary>
		public float BoundsDiagonalMeters { get; }
	}

	/// <summary>
	/// The pinhole projection a visibility test needs, in pixels.
	/// </summary>
	internal readonly struct CameraProjection
	{
		internal CameraProjection(float focalX, float focalY, float principalX,
			float principalY, int width, int height)
		{
			FocalX = focalX;
			FocalY = focalY;
			PrincipalX = principalX;
			PrincipalY = principalY;
			Width = width;
			Height = height;
		}

		internal float FocalX { get; }
		internal float FocalY { get; }
		internal float PrincipalX { get; }
		internal float PrincipalY { get; }
		internal int Width { get; }
		internal int Height { get; }

		internal bool IsUsable => Width > 0 && Height > 0 && FocalX > 0f && FocalY > 0f;

		/// <summary>
		/// Reads the projection the tracker is currently using.
		/// </summary>
		/// <remarks>
		/// Two conventions of this entry point are easy to get wrong and both are
		/// pinned here. It reports availability as a <b>flag</b> — non-zero means the
		/// active colour camera answered — and not as an <see cref="FTErrorCode"/>,
		/// where zero is success. And it reports focal length and principal point
		/// already <b>in pixels</b>, which is the same form
		/// <c>XRTrackerManager.CachedFu</c> and the projection-matrix update use;
		/// the normalized form appears only in recorded sequence metadata.
		/// </remarks>
		internal static bool TryRead(out CameraProjection projection)
		{
			projection = default;
			int available = FTBridge.FT_GetCameraIntrinsics(out float focalX, out float focalY,
				out float principalX, out float principalY, out int width, out int height);
			if (available == 0 || width <= 0 || height <= 0)
				return false;

			projection = new CameraProjection(
				focalX, focalY, principalX, principalY, width, height);
			return projection.IsUsable;
		}

		/// <summary>
		/// Whether a point given in the camera's own frame lands inside the image.
		/// </summary>
		/// <param name="cameraSpacePoint">
		/// The point in Unity camera space: X right, Y up, Z forward.
		/// </param>
		internal bool ProjectsInside(Vector3 cameraSpacePoint)
		{
			if (!(cameraSpacePoint.z > 0f))
				return false;

			// The tracker's image frame counts rows downwards while Unity's camera
			// frame counts them upwards, so the vertical axis is flipped before the
			// principal point is applied.
			float u = PrincipalX + FocalX * (cameraSpacePoint.x / cameraSpacePoint.z);
			float v = PrincipalY + FocalY * (-cameraSpacePoint.y / cameraSpacePoint.z);
			return u >= 0f && u < Width && v >= 0f && v < Height;
		}
	}

	internal static class ViewVisibility
	{
		/// <summary>
		/// Whether a box could have any part of itself inside the camera's view.
		/// </summary>
		/// <remarks>
		/// <para>This is the decision the coast is allowed to act on, and it is
		/// deliberately one-sided: it returns false only when the box is provably
		/// outside, and returns true whenever it cannot prove that. The corner
		/// fraction cannot be used for this, because a box can have every corner
		/// outside the image and still cover it — a box larger than the view, a
		/// camera standing inside the box, and a box straddling the near plane are
		/// all cases where every corner fails and the body fills the screen.</para>
		/// <para>The test is the standard one: the box is outside if, for some one
		/// bounding plane of the view, all eight corners are on its outer side.
		/// Failing to find such a plane does not prove intersection, so the answer
		/// errs towards "visible" — which is the direction that costs nothing,
		/// because it only ever declines to coast.</para>
		/// </remarks>
		internal static bool CouldBeInView(
			CameraProjection projection,
			Matrix4x4 cameraFromWorld,
			Matrix4x4 worldFromBody,
			Bounds localBounds)
		{
			Matrix4x4 cameraFromBody = cameraFromWorld * worldFromBody;
			Vector3 centre = localBounds.center;
			Vector3 extents = localBounds.extents;

			// Each plane is a half-space through the camera origin, written so that a
			// point is on the inner side when the dot product is not negative. The
			// first is the camera's own optical boundary; the other four are the
			// image borders carried back through the projection. None of them
			// involves a chosen number.
			for (int planeIndex = 0; planeIndex < BOUNDING_PLANE_COUNT; ++planeIndex)
			{
				Vector3 plane = BoundingPlane(projection, planeIndex);
				bool allOutside = true;
				for (int corner = 0; corner < 8 && allOutside; ++corner)
				{
					Vector3 local = CornerOf(centre, extents, corner);
					if (Vector3.Dot(plane, cameraFromBody.MultiplyPoint3x4(local)) >= 0f)
						allOutside = false;
				}
				if (allOutside)
					return false;
			}

			return true;
		}

		private const int BOUNDING_PLANE_COUNT = 5;

		// Each plane is built when it is asked for, so the visibility check allocates nothing.
		private static Vector3 BoundingPlane(CameraProjection projection, int planeIndex)
		{
			switch (planeIndex)
			{
				case 0:
					return new Vector3(0f, 0f, 1f);
				case 1:
					return new Vector3(projection.FocalX, 0f, projection.PrincipalX);
				case 2:
					return new Vector3(-projection.FocalX, 0f, projection.Width - projection.PrincipalX);
				case 3:
					return new Vector3(0f, -projection.FocalY, projection.PrincipalY);
				default:
					return new Vector3(0f, projection.FocalY, projection.Height - projection.PrincipalY);
			}
		}

		private static Vector3 CornerOf(Vector3 centre, Vector3 extents, int corner)
		{
			return new Vector3(
				centre.x + ((corner & 1) == 0 ? -extents.x : extents.x),
				centre.y + ((corner & 2) == 0 ? -extents.y : extents.y),
				centre.z + ((corner & 4) == 0 ? -extents.z : extents.z));
		}

		/// <summary>
		/// The share of a box's corners that project into the image. <b>Diagnostic
		/// only</b> — coast eligibility uses <see cref="CouldBeInView"/>, because a
		/// corner count can read zero on a box that covers the whole image.
		/// </summary>
		/// <param name="cameraFromWorld">The inverse of the camera's world pose.</param>
		/// <param name="worldFromBody">Where the body is believed to be.</param>
		/// <param name="localBounds">The body's extent in its own frame.</param>
		internal static float InViewFraction(
			CameraProjection projection,
			Matrix4x4 cameraFromWorld,
			Matrix4x4 worldFromBody,
			Bounds localBounds)
		{
			Vector3 centre = localBounds.center;
			Vector3 extents = localBounds.extents;
			Matrix4x4 cameraFromBody = cameraFromWorld * worldFromBody;

			int inside = 0;
			for (int corner = 0; corner < 8; ++corner)
			{
				Vector3 local = CornerOf(centre, extents, corner);
				if (projection.ProjectsInside(cameraFromBody.MultiplyPoint3x4(local)))
					++inside;
			}

			return inside / 8f;
		}
	}
}
