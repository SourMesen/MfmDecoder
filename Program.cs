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
			foreach(string wavFile in Directory.GetFiles("./", "*.wav")) {
				DecodeFile(wavFile);
			}
		}

		private static void DecodeFile(string wavFile)
		{
			List<short> samples = new List<short>(30000);
			int wavHeaderSize = 44;
			byte[] data = File.ReadAllBytes(wavFile);
			for(int i = wavHeaderSize; i < data.Length; i += 4) {
				short value = (short)(data[i + 2] | (data[i + 3] << 8));
				samples.Add(value);
			}

			Func<int, bool> isClock = (int i) => {
				//If the signal doesn't go up or down at least 500 samples within 3 samples, this can't be a clock
				if(Math.Abs(samples[i] - samples[i - 3]) < 500) {
					return false;
				}
				if(Math.Abs(samples[i] - samples[i + 3]) < 500) {
					return false;
				}

				//If the signal doesn't go the same direction on both sides, it can't be a clock
				if(Math.Sign(samples[i] - samples[i - 3]) != Math.Sign(samples[i] - samples[i + 3])) {
					return false;
				}

				if(samples[i] >= samples[i - 1] && samples[i] >= samples[i + 1] && samples[i - 1] > samples[i - 2] && samples[i + 1] > samples[i + 2]) {
					return true;
				} else if(samples[i] <= samples[i - 1] && samples[i] <= samples[i + 1] && samples[i - 1] < samples[i - 2] && samples[i + 1] < samples[i + 2]) {
					return true;
				}

				return false;
			};

			//const double samplesPerBit = 44100 / 4800.0;

			int pageCount = 0;
			int lastClock = 0;
			int lastBit = 0;
			bool dataStarted = false;
			bool expectZero = false;
			byte currentByte = 0;
			int bitCounter = 0;
			bool failed = false;
			List<byte> rawData = new List<byte>();
			List<List<byte>> pages = new List<List<byte>>();

			Action savePage = () => {
				if(rawData.Count > 0) {
					pages.Add(rawData);
					string filename = Path.GetFileNameWithoutExtension(wavFile) + "\\" + "Page" + pageCount;
					Directory.CreateDirectory(Path.GetFileNameWithoutExtension(wavFile));
					if(rawData[0] != 0xC5) {
						filename += ".InvalidHeader";
					}
					if(failed) {
						filename += ".BadData";
					}
					File.WriteAllBytes(filename + ".bin", rawData.ToArray());
					pageCount++;
				}

				rawData = new List<byte>();
				lastBit = 0;
				dataStarted = false;
				expectZero = false;
				currentByte = 0;
				bitCounter = 0;
			};

			Action<int> addBit = (int bit) => {
				if(expectZero) {
					if(bit != 0) {
						throw new Exception("0 bit expected (start of byte marker)");
					} else {
						bitCounter = 8;
						expectZero = false;
					}
				} else if(bitCounter > 0) {
					bitCounter--;
					currentByte |= (byte)(bit << bitCounter);

					if(bitCounter == 0) {
						//System.Diagnostics.Debug.WriteLine("Full byte: " + currentByte.ToString("X2"));
						rawData.Add(currentByte);
						currentByte = 0;
						expectZero = true;
					}
				}
				//System.Diagnostics.Debug.WriteLine("Bit: " + (bit == 1 ? "1" : "0") + " Counter: " + dataCounter + " Zero: " + (expectZero ? "T" : "F") + " Data: " + currentByte.ToString("X2"));
				lastBit = bit;
			};

			for(int i = 3; i < samples.Count - 3; i++) {
				if(isClock(i)) {
					if(isClock(i + 1)) {
						i++;
					}

					int timeGap = i - lastClock;
					lastClock = i;

					if(timeGap > 70000) {
						//A large gap was detected, write current data to disk and start a new page
						savePage();
						lastBit = 0;
						failed = false;
						continue;
					}

					if(failed) {
						//Wait until we find a large gap before restarting
						continue;
					}

					//System.Diagnostics.Debug.WriteLine("Clock: " + i.ToString() + "  Gap: " + timeGap.ToString());
					try {
						if(timeGap >= 20 || timeGap < 6) {
							throw new Exception("unexpected clock");
						} else {
							if(lastBit == 1) {
								if(timeGap <= 10) {
									//6-10 sample gap: 1-1
									addBit(1);
								} else if(timeGap <= 15) {
									//13-15 sample gap: 1-0-0
									addBit(0);
									addBit(0);
								} else {
									//16-19 sample gap: 1-0-1
									addBit(0);
									addBit(1);
								}
							} else {
								if(timeGap <= 10) {
									//6-10 sample gap: 0-0
									addBit(0);
								} else if(timeGap <= 15) {
									//13-15 sample gap: 0-1
									addBit(1);

									if(!dataStarted) {
										//If this is the first 1 bit we find, the next bit should be a 0 and then the first byte starts
										dataStarted = true;
										expectZero = true;
										currentByte = 0;
									}
								} else {
									//16-19 sample gap (4 half bits)
									//Should not be possible after a 0 value
									throw new Exception("unexpected delay");
								}
							}
						}
					} catch {
						//An exception occurred (too small/large gap, or invalid 0 bit + 8 data bits pattern
						//Clear everything up and try again
						System.Diagnostics.Debug.WriteLine("Failed to process page");
						failed = true;
					}
				}
			}

			savePage();
		}
	}
}
