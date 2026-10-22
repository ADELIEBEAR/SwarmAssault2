// Developer-only process fixture. Not included in the installer.
using System;
using System.IO;
using System.Text;
class StubGame { static void Main(string[] args) { File.WriteAllLines(Environment.GetEnvironmentVariable("SII_STUB_OUTPUT"),args,new UTF8Encoding(false)); } }
