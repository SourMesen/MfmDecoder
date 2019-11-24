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

		List<int> _headerBits = new List<int>();
		List<byte> _rawData = new List<byte>();
		List<List<byte>> _pages = new List<List<byte>>();

		public MfmDecoder(string wavFile)
		{
			System.Diagnostics.Debug.WriteLine("Processing: " + Path.GetFileName(wavFile));
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
				_pageCount++;
				string filename = Path.GetFileNameWithoutExtension(_wavFile) + "\\" + "Page" + _pageCount;
				Directory.CreateDirectory(Path.GetFileNameWithoutExtension(_wavFile));
				if(_rawData[0] != 0xC5) {
					filename += ".InvalidHeader";
				}
				if(_failed) {
					filename += ".BadData";
				}
				File.WriteAllBytes(filename + ".bin", _rawData.ToArray());
			}

			_rawData = new List<byte>();
			_lastBit = 0;
			_dataStarted = false;
			_expectZero = false;
			_currentByte = 0;
			_bitCounter = 0;
			_headerBits = new List<int>();
		}

		private void AddBit(int bit)
		{
			if(_dataStarted) {
				if(_expectZero) {
					if(bit != 0) {
						if(_dataStarted) {
							//throw new Exception("0 bit expected (start of byte marker)");
							System.Diagnostics.Debug.WriteLine("0 bit expected (start of byte marker)");
							_failed = true;
						}
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
			} else {
				_headerBits.Add(bit);
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
					if(timeGap < 6) {
						//Ignore this peak, probably a false positive
						if(_dataStarted) {
							System.Diagnostics.Debug.WriteLine("Gap between clocks too small, ignoring clock: " + i.ToString());
						}
						continue;
					}

					_lastClock = i;

					if(timeGap > 70000) {
						//A large gap was detected, write current data to disk and start a new page
						SavePage();
						_failed = false;
						continue;
					}

					/*if(_failed) {
						//Wait until we find a large gap before restarting
						continue;
					}*/

					//System.Diagnostics.Debug.WriteLine("Clock: " + i.ToString() + "  Gap: " + timeGap.ToString());
					try {
						if(timeGap > 1000) {
							//Try to find another 0 followed by 1 sequence
							_dataStarted = false;
							_headerBits = new List<int>();
						} else if(timeGap > 20) {
							if(_dataStarted) {
								System.Diagnostics.Debug.WriteLine("Gap too large: " + i.ToString());
								_failed = true;
								//throw new Exception("unexpected clock");
							}
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

									if(!_dataStarted && _headerBits.Count > 10) {
										bool validTrackStart = true;
										for(int j = 0; j < 10; j++) {
											if(_headerBits[_headerBits.Count - 2 - j] != 0) {
												validTrackStart = false;
												break;
											}
										}

										if(validTrackStart) {
											//If this is the first 1 bit we find, the next bit should be a 0 and then the first byte starts
											System.Diagnostics.Debug.WriteLine("Started new data track at: " + i.ToString());
											_dataStarted = true;
											_expectZero = true;
											_currentByte = 0;
										} else {
											_lastBit = 0;
										}
									}
								} else {
									//16-19 sample gap (4 half bits)
									//Should not be possible after a 0 value
									if(_dataStarted) {
										System.Diagnostics.Debug.WriteLine("Gap too large (after 0): " + i.ToString());
										//throw new Exception("unexpected delay");
										_failed = true;
										//AddBit(1);
									}
								}
							}
						}
					} catch {
						//An exception occurred (too small/large gap, or invalid 0 bit + 8 data bits pattern)
						System.Diagnostics.Debug.WriteLine("Failed to process page");
						_failed = true;
					}

					if(!_dataStarted) {
						_lastBit = 0;
					}
				}
			}

			SavePage();
		}
	}
}
