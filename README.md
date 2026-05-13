# Introduction

A full cross-platform audio library for desktop OSes (Windows, mac, Linux), entirely in C#. Requires .NET 10.

# Technicals

Integrates ports of C and C++ from DOSBox Staging and SDL2, Speex, IIR Filters, native interops, and more, so Spice86.Core can focus on emulated audio devices (Sound Blaster, OPL2, OPL3, Adlib Gold, ...)

## License and Credits

This project makes use of parts of the following third-party software, as C# ports:

- **SDL (Simple DirectMedia Layer)**
  - Licensed under the [zlib License](LICENSE.SDL).
  - Full thanks to the SDL team for their outstanding cross-platform multimedia library.  This C# port would not exist otherwise.

- **DOSBox Staging**
  - Licensed under the [GNU GPL v2.0](LICENSE.DOSBOXSTAGING).
  - thanks to the DOSBox Staging team and DOSBox teams. This C# port would not exist otherwise.

Also present as ported C# code:

- **IIR Filters**
- **Speex Resampler**
- A lot of work from Patrick Kunz, Martin Eastwood, John Novak, and more, have also been ported as C# code.

Please see the respective LICENSE files for more details.
