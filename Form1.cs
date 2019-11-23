using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MfmDecoder
{
	public partial class Form1 : Form
	{
		public Form1()
		{
			InitializeComponent();

			List<short> samples = new List<short>(30000);
			byte[] data = File.ReadAllBytes("data.raw");
			for(int i = 0; i < data.Length; i += 4) {
				short value = (short)(data[i + 2] | (data[i + 3] << 8));
				samples.Add(value);
			}

			Func<int, bool> isClock = (int i) => {
				if(Math.Abs(samples[i] - samples[i - 3]) < 500) {
					return false;
				}
				if(Math.Abs(samples[i] - samples[i + 3]) < 500) {
					return false;
				}
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

			const double samplesPerBit = 44100 / 4800.0;

			int lastClock = 0;
			int lastBit = 0;
			bool dataStarted = false;
			bool expectZero = false;
			byte currentByte = 0;
			int bitCounter = 0;
			List<byte> rawData = new List<byte>();
			List<List<byte>> pages = new List<List<byte>>();

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
					if(timeGap > 10000) {
						//A large gap was detected, write current data to disk and start a new page
						if(rawData.Count > 0) {
							pages.Add(rawData);
							File.WriteAllBytes("Page" + pages.Count + ".bin", rawData.ToArray());
							rawData = new List<byte>();
							lastClock = 0;
							lastBit = 0;
							dataStarted = false;
							expectZero = false;
							currentByte = 0;
							bitCounter = 0;
						}
					}

					//System.Diagnostics.Debug.WriteLine("Clock: " + i.ToString() + "  Gap: " + timeGap.ToString());
					if(lastClock > 0) {
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
					} else {
						addBit(0);
					}

					lastClock = i;
				}
			}
		}
	}
}
