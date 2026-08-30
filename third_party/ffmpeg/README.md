# Bundled FFmpeg runtime

CloudLight Video Compressor redistributes the following Windows x64 static build as a runtime dependency:

- Version: 9.0.1-full_build-www.gyan.dev
- Provider: [Gyan.dev FFmpeg builds](https://www.gyan.dev/ffmpeg/builds/)
- Package: Gyan.FFmpeg 9.0.1 full build
- Architecture: Windows x64, static build
- License: GPL-3.0; the complete license text is in [LICENSE.txt](LICENSE.txt)
- Upstream FFmpeg source revision: [bf1b838f2a](https://github.com/FFmpeg/FFmpeg/commit/bf1b838f2a)

The build process reads only this checked-in third_party/ffmpeg directory. It never relies on a WinGet cache or another machine-specific FFmpeg path.

| File | SHA-256 |
| --- | --- |
| ffmpeg.exe | 57C56E369D5B4873B4D93FC1A1D833CB7CD8BC9325C14B05C34CE60B22842D8A |
| ffprobe.exe | AFE05347CAAABE479B3C4EAE71992B6EC1E11C57266A1D665DEB0F9FE9847208 |

ffmpeg.exe -version and ffprobe.exe -version are checked by build-installer.ps1 before publishing.
