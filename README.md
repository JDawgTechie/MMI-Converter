![GitHub Release Downloads](https://img.shields.io/github/downloads/JDawgTechie/MMI-Converter/total?color=a855f7)

# MMI Converter

MMI Converter is a Windows utility for converting original LaserActive `.mmi` disc images to smaller Motion JPEG `.mjmmi` playback images for the Sega Mega-LD and NEC LD-ROM² PACs.

## Downloads

- [MMI Converter releases](https://github.com/JDawgTechie/MMI-Converter/releases)
- [ares with Motion JPEG LaserActive support](https://github.com/JDawgTechie/ares/releases)
- [.NET 10 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## Usage

1. Open or drag one or more Sega Mega-LD or NEC LD-ROM² `.mmi` files into the queue.
2. Wait for the size estimates and select a JPEG quality from 80 to 100. **Quality 94** is the recommended starting point.
3. Convert the discs. Each output keeps its original base name and is saved beside the source with the `.mjmmi` extension. If that destination already exists, the converter reports a conflict and preserves the existing file.
4. Open the converted discs in the Motion JPEG ares build linked above.

Original captures with 910 × 525 RGB QON video are supported. Conversion preserves game and audio data and compresses the video. Keep the original `.mmi` files for archival use because the video compression is lossy. A conversion temporarily needs space for encoded data beside the source.

## Building

Open `MMI Converter.slnx` in Visual Studio with the .NET desktop development workload and .NET 10 SDK. Build the solution in Release. The single-file executable appears in the project's `bin\Release` folder. It requires the x64 .NET 10 Desktop Runtime.

Keep `Native` and `ThirdParty` with the project. They contain the native build inputs, license notices, and the FFmpeg source archive. A normal Release build uses Visual Studio's shared NuGet cache and bundles the app as part of the build.

The project builds with the included native binaries. To rebuild the encoder, use `Native/Build-Encoder.ps1` with the MSYS2 CLANG64 toolchain. The included libjpeg-turbo static library and header come from the CLANG64 libjpeg-turbo 3.2.1 package. See `ThirdParty/Build-FFmpeg.sh` for the FFmpeg build.

## Licenses and third-party software

The application is licensed under the [MIT License](LICENSE). Third-party components retain their own licenses.

This software uses [FFmpeg](https://ffmpeg.org/) under the [GNU LGPL 2.1 or later](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html). The [matching FFmpeg 9.1 source archive and build recipe](MMI%20Converter/ThirdParty/FFmpeg-source.zip) are available here and through the in-app Settings information button.

libjpeg-turbo encodes the JPEG frames, and FFmpeg independently checks sampled output frames. The native helpers are embedded in the application and launched from a temporary directory, so a separate FFmpeg installation is unnecessary.

See the [third-party notices](MMI%20Converter/ThirdParty/Notices.txt) for details and attribution, including the MIT-licensed MMI/QON utilities from MukiEX's mmidsc project.
