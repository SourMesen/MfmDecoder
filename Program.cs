using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MfmDecoder
{
	static class Program
	{
		static void Main(string[] argv)
		{
			if(argv.Length == 0) {
				argv = Directory.GetFiles("./", "*.wav").ToArray();
			}

			foreach(string wavFile in argv) {
				new MfmDecoder(wavFile).Decode();
			}
		}
	}
}
