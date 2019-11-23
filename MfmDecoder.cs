using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MfmDecoder
{
	class MfmDecoder
	{
		string _wavFile;
		short[] _samples;

		int _lastClock = 0;
		int _lastBit = 0;
		bool _dataStarted = false;
		bool _expectZero = false;
		byte _currentByte = 0;
		int _bitCounter = 0;

		bool _failed = false;
		int _pageCount = 0;

		List<byte> _rawData = new List<byte>();
		List<List<byte>> _pages = new List<List<byte>>();

		public MfmDecoder(string wavFile)
		{
			_wavFile = wavFile;

			int wavHeaderSize = 44;
			using(FileStream input = File.OpenRead(_wavFile)) {
				input.Seek(44, SeekOrigin.Begin);

				int sampleCount = (int)(input.Length - wavHeaderSize) / 4;
				_samples = new short[sampleCount];

				byte[] readBuffer = new byte[32768];
				int samplesRead = 0;
				int bytesRead;
				do {
					bytesRead = input.Read(readBuffer, 0, 32768);

					for(int i = 0; i < bytesRead; i += 4) {
						short value = (short)(readBuffer[i + 2] | (readBuffer[i + 3] << 8));
						_samples[samplesRead] = value;
						samplesRead++;
					}
				} while(bytesRead == 32768);
			}
		}

		private void SavePage()
		{
			if(_rawData.Count > 0) {
				_pages.Add(_rawData);
				string filename = Path.GetFileNameWithoutExtension(_wavFile) + "\\" + "Page" + _pageCount;
				Directory.CreateDirectory(Path.GetFileNameWithoutExtension(_wavFile));
				if(_rawData[0] != 0xC5) {
					filename += ".InvalidHeader";
				}
				if(_failed) {
					filename += ".BadData";
				}
				File.WriteAllBytes(filename + ".bin", _rawData.ToArray());
				_pageCount++;
			}

			_rawData = new List<byte>();
			_lastBit = 0;
			_dataStarted = false;
			_expectZero = false;
			_currentByte = 0;
			_bitCounter = 0;
		}

		private void AddBit(int bit)
		{
			if(_expectZero) {
				if(bit != 0) {
					throw new Exception("0 bit expected (start of byte marker)");
				} else {
					_bitCounter = 8;
					_expectZero = false;
				}
			} else if(_bitCounter > 0) {
				_bitCounter--;
				_currentByte |= (byte)(bit << _bitCounter);

				if(_bitCounter == 0) {
					//System.Diagnostics.Debug.WriteLine("Full byte: " + currentByte.ToString("X2"));
					_rawData.Add(_currentByte);
					_currentByte = 0;
					_expectZero = true;
				}
			}
			//System.Diagnostics.Debug.WriteLine("Bit: " + (bit == 1 ? "1" : "0") + " Counter: " + dataCounter + " Zero: " + (expectZero ? "T" : "F") + " Data: " + currentByte.ToString("X2"));
			_lastBit = bit;
		}

		private bool IsClock(int i)
		{
			//If the signal doesn't go up or down at least 500 samples within 3 samples, this can't be a clock
			if(Math.Abs(_samples[i] - _samples[i - 3]) < 500) {
				return false;
			}
			if(Math.Abs(_samples[i] - _samples[i + 3]) < 500) {
				return false;
			}

			//If the signal doesn't go the same direction on both sides, it can't be a clock
			if(Math.Sign(_samples[i] - _samples[i - 3]) != Math.Sign(_samples[i] - _samples[i + 3])) {
				return false;
			}

			if(_samples[i] >= _samples[i - 1] && _samples[i] >= _samples[i + 1] && _samples[i - 1] > _samples[i - 2] && _samples[i + 1] > _samples[i + 2]) {
				return true;
			} else if(_samples[i] <= _samples[i - 1] && _samples[i] <= _samples[i + 1] && _samples[i - 1] < _samples[i - 2] && _samples[i + 1] < _samples[i + 2]) {
				return true;
			}

			return false;
		}

		public void Decode()
		{
			for(int i = 3; i < _samples.Length - 3; i++) {
				if(IsClock(i)) {
					if(IsClock(i + 1)) {
						i++;
					}

					int timeGap = i - _lastClock;
					_lastClock = i;

					if(timeGap > 70000) {
						//A large gap was detected, write current data to disk and start a new page
						SavePage();
						_lastBit = 0;
						_failed = false;
						continue;
					}

					if(_failed) {
						//Wait until we find a large gap before restarting
						continue;
					}

					//System.Diagnostics.Debug.WriteLine("Clock: " + i.ToString() + "  Gap: " + timeGap.ToString());
					try {
						if(timeGap >= 20 || timeGap < 6) {
							throw new Exception("unexpected clock");
						} else {
							if(_lastBit == 1) {
								if(timeGap <= 10) {
									//6-10 sample gap: 1-1
									AddBit(1);
								} else if(timeGap <= 15) {
									//13-15 sample gap: 1-0-0
									AddBit(0);
									AddBit(0);
								} else {
									//16-19 sample gap: 1-0-1
									AddBit(0);
									AddBit(1);
								}
							} else {
								if(timeGap <= 10) {
									//6-10 sample gap: 0-0
									AddBit(0);
								} else if(timeGap <= 15) {
									//13-15 sample gap: 0-1
									AddBit(1);

									if(!_dataStarted) {
										//If this is the first 1 bit we find, the next bit should be a 0 and then the first byte starts
										_dataStarted = true;
										_expectZero = true;
										_currentByte = 0;
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
						_failed = true;
					}
				}
			}

			SavePage();
		}
	}
}
