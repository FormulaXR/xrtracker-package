# XRTracker

AR object tracking plugin for Unity. Track real-world objects in real time using your device's camera.

## Supported Platforms

- **iOS** — AR Foundation with ARKit
- **Android** — AR Foundation with ARCore
- **Meta Quest 3/3S** — Passthrough Camera API (Horizon OS v74+)
- **Desktop** — Webcam support for development

## Installation

Add to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.formulaxr.tracker": "https://github.com/FormulaXR/xrtracker-package.git#release"
  }
}
```

To pin a specific version:

```json
{
  "dependencies": {
    "com.formulaxr.tracker": "https://github.com/FormulaXR/xrtracker-package.git#v1.0.0"
  }
}
```

> **Note:** This package uses Git LFS for native plugins. Ensure [Git LFS](https://git-lfs.github.com/) is installed on your machine.

## Quick Start

1. Add `FormulaTrackingManager` to your scene
2. Add `TrackedBody` component to objects you want to track
3. Configure the mesh and tracking settings
4. Add the appropriate camera feeder for your platform:
   - `ARFoundationCameraFeeder` for iOS/Android
   - `QuestCameraFeeder` for Meta Quest 3/3S
   - `FormulaWebcamFeeder` for desktop

## Platform-Specific Setup

### AR Foundation (iOS/Android)

Requires the `com.unity.xr.arfoundation` package. The `HAS_AR_FOUNDATION` scripting define is automatically set when AR Foundation is present.

### Meta Quest 3/3S

Requires:
- Quest 3 or Quest 3S hardware
- Horizon OS v74 or later
- Camera permissions in your `AndroidManifest.xml`:
```xml
<uses-permission android:name="horizonos.permission.HEADSET_CAMERA" />
```

## Documentation

Full documentation, guides, and API reference at **[docs.xrtracker.com](https://docs.xrtracker.com)**.

## Support

- Documentation: [docs.xrtracker.com](https://docs.xrtracker.com)
- Email: [support@formulaxr.com](mailto:support@formulaxr.com)
- Discord: [Developer Community](https://discord.gg/dtHEbC9V)
- Issues: [GitHub Issues](https://github.com/FormulaXR/xrtracker-package/issues)

## License

Proprietary — [FormulaXR](https://xrproj.com). See [LICENSE](LICENSE) for details.
