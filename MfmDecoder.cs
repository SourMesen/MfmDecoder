﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MfmDecoder
{
	class MfmDecoder
	{
		Dictionary<int, short[]> _filteredSamples = new Dictionary<int, short[]>();
		string _wavFile;
		short[] _originalSamples;
		short[] _samples;

		int _lastClock = 0;
		int _lastBit = 0;
		bool _dataStarted = false;
		bool _expectZero = false;
		byte _currentByte = 0;
		int _bitCounter = 0;
		int _dataStartPosition = 0;
		bool _allPagesValid = true;

		int _lastSavePosition = 0;
		int _retryCount = 0;

		bool _failed = false;
		List<int> _failedIndex = new List<int>();
		int _pageCount = 0;
		int _cutoffFreq = 0;

		int _zeroBitCounter = 0;
		int _leadInPosition = 0;

		List<byte> _rawData = new List<byte>();
		List<PageInfo> _pages = new List<PageInfo>();

		StringBuilder _pageLog = new StringBuilder();
		StringBuilder _errorLog = new StringBuilder();

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

			_originalSamples = _samples;
		}

		private void Reset()
		{
			_cutoffFreq = 0;
			_rawData = new List<byte>();
			_lastBit = 0;
			_dataStarted = false;
			_expectZero = false;
			_currentByte = 0;
			_bitCounter = 0;
			_zeroBitCounter = 0;
			_failed = false;
			_samples = _originalSamples;
		}

		private void Log(string message)
		{
			if(_cutoffFreq == 0) {
				_pageLog.AppendLine(message);
			}
		}

		private void SavePage(int currentSamplePosition)
		{
			_lastSavePosition = currentSamplePosition;

			if(_rawData.Count > 0) {
				_pageCount++;
				string filepath = Path.GetFileNameWithoutExtension(_wavFile) + "\\" + "Page" + _pageCount;
				if(_rawData[0] != 0xC5) {
					filepath += ".InvalidHeader";
					Log("Page index " + _pages.Count + " failed - bad header (Start: " + _dataStartPosition + ")");
					_allPagesValid = false;
				}
				if(_failed) {
					Log("Page index " + _pages.Count + " failed (Start: " + _dataStartPosition + ")");
					filepath += ".BadData";
					_allPagesValid = false;
				}

				if(_leadInPosition > _dataStartPosition) {
					_allPagesValid = false;
					Log("Lead-in should be before data start position");
				}

				//int pageNumber = _rawData.Count > 5 ? _rawData[5] : -100;

				_pages.Add(new PageInfo() {
					FileName = Path.GetFileName(filepath + ".bin"),
					StartPosition = _dataStartPosition,
					LeadInPosition = _leadInPosition,
					Data = _rawData
				});
				//System.Diagnostics.Debug.WriteLine("Writing: " + filepath + " (Number=" + pageNumber.ToString() + ").bin");
				if(_failed || _rawData[0] != 0xC5) {
					_failedIndex.Add(_pages.Count - 1);
					System.Diagnostics.Debug.WriteLine("Page Index " + (_pages.Count - 1) + " failed (Start: " + _dataStartPosition + ")");
				} else {
					//System.Diagnostics.Debug.WriteLine("Page Index " + pageNumber.ToString() + " passed (" + _cutoffFreq.ToString() + ")");
				}
				//Directory.CreateDirectory(Path.GetFileNameWithoutExtension(_wavFile));
				//File.WriteAllBytes(filepath + " (Number=" + pageNumber.ToString() + ").bin", _rawData.ToArray());

				_leadInPosition = 0;
				_dataStartPosition = 0;
				Log("----");
			}

			_errorLog.Append(_pageLog.ToString());
			_pageLog.Clear();
			Reset();
		}

		private void AddBit(int bit, int i)
		{
			if(_dataStarted) {
				if(_expectZero) {
					if(bit != 0) {
						if(_dataStarted) {
							Log("0 bit expected (start of byte marker) (Position: " + i + ")");
							_failed = true;
							_expectZero = false;
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
			}
			//System.Diagnostics.Debug.WriteLine("Bit: " + (bit == 1 ? "1" : "0") + " Counter: " + dataCounter + " Zero: " + (expectZero ? "T" : "F") + " Data: " + currentByte.ToString("X2"));
			_lastBit = bit;
		}

		enum eClockType
		{
			None,
			High,
			Low			
		}

		private eClockType IsClock(int i)
		{
			//If the signal doesn't go up or down at least 500 samples within 3 samples, this can't be a clock
			if(Math.Abs(_samples[i] - _samples[i - 3]) < 500) {
				return eClockType.None;
			}
			if(Math.Abs(_samples[i] - _samples[i + 3]) < 500) {
				return eClockType.None;
			}

			//If the signal doesn't go the same direction on both sides, it can't be a clock
			if(Math.Sign(_samples[i] - _samples[i - 3]) != Math.Sign(_samples[i] - _samples[i + 3])) {
				return eClockType.None;
			}

			if(_samples[i] >= _samples[i - 1] && _samples[i] >= _samples[i + 1] && _samples[i - 1] > _samples[i - 2] && _samples[i + 1] > _samples[i + 2]) {
				return eClockType.High;
			} else if(_samples[i] <= _samples[i - 1] && _samples[i] <= _samples[i + 1] && _samples[i - 1] < _samples[i - 2] && _samples[i + 1] < _samples[i + 2]) {
				return eClockType.Low;
			}

			return eClockType.None;
		}

		private void ApplyFilter()
		{
			int cutoffFreq = 250 * _retryCount;
			_cutoffFreq = cutoffFreq;

			//System.Diagnostics.Debug.WriteLine("Attempting with " + cutoffFreq + " hz high pass filter");

			if(_filteredSamples.ContainsKey(cutoffFreq)) {
				_samples = _filteredSamples[cutoffFreq];
				return;
			}

			double sampleRate = 44100.0;

			double rc = 1.0 / (2 * Math.PI * cutoffFreq);
			double a = rc / (rc + (1 / sampleRate));

			short[] result = new short[_originalSamples.Length];
			result[0] = _originalSamples[0];
			for(int i = 1; i < _originalSamples.Length - 1; i++) {
				result[i] = (short)(a * (result[i - 1] + _originalSamples[i] - _originalSamples[i + 1]));
			}

			_filteredSamples[cutoffFreq] = result;
			_samples = result;
		}

		public void Decode()
		{
			string waveName = Path.GetFileNameWithoutExtension(_wavFile);

			eClockType lastClockType = eClockType.None;
			for(int i = 3; i < _samples.Length - 3; i++) {
				//Ignore 2 clocks in the same direction in a row, this is usually a false positive
				eClockType clockType = IsClock(i);
				if(clockType != eClockType.None && clockType != lastClockType) {
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
						if(_retryCount < 2 && (_failed || (_rawData.Count > 0 && _rawData[0] != 0xC5))) {
							//If the current page is invalid, try applying a filter and trying again
							_retryCount++;
							Reset();
							ApplyFilter();
							i = _lastSavePosition - 1;
							_lastClock = i - 1;
							continue;
						}

						_retryCount = 0;
						SavePage(i);
						continue;
					}

					lastClockType = clockType;

					//Log("Clock: " + i.ToString() + "  Gap: " + timeGap.ToString());

					try {
						if(timeGap > 1000) {
							//Try to find another 0 followed by 1 sequence
							if(_retryCount < 2 && (_failed || (_rawData.Count > 0 && _rawData[0] != 0xC5))) {
								//If the current page is invalid, try applying a filter and trying again
								_retryCount++;
								Reset();
								ApplyFilter();
								i = _lastSavePosition - 1;
								_lastClock = i - 1;
								continue;
							}
							_retryCount = 0;
							SavePage(i);
						} else if(timeGap > 20) {
							if(_dataStarted) {
								Log("Gap too large: " + i.ToString() + " (Gap: " + timeGap + ")");
								_failed = true;
							}
						} else {
							if(_lastBit == 1) {
								if(timeGap <= 11) {
									//6-10 sample gap: 1-1
									AddBit(1, i);
								} else if(timeGap <= 15) {
									//13-15 sample gap: 1-0-0
									AddBit(0, i);
									AddBit(0, i);
								} else {
									//16-19 sample gap: 1-0-1
									AddBit(0, i);
									AddBit(1, i);
								}
								_zeroBitCounter = 0;
							} else {
								if(timeGap <= 11) {
									//6-10 sample gap: 0-0
									AddBit(0, i);
									if(!_dataStarted) {
										//Track lead-in start & length
										_zeroBitCounter++;
										if(_zeroBitCounter == 1) {
											_leadInPosition = i;
										}
									}
								} else if(timeGap <= 15) {
									//13-15 sample gap: 0-1
									AddBit(1, i);

									if(!_dataStarted) {
										if(_zeroBitCounter > 10) {
											//If this is the first 1 bit we find, the next bit should be a 0 and then the first byte starts
											Log("Started new data track at: " + i.ToString());
											_dataStarted = true;
											if(_dataStartPosition == 0) {
												_dataStartPosition = i;
											}
											_expectZero = true;
											_currentByte = 0;
										} else {
											_lastBit = 0;
										}
									}

									_zeroBitCounter = 0;
								} else {
									//16-19 sample gap (4 half bits)
									//Should not be possible after a 0 value
									_zeroBitCounter = 0;
									if(_dataStarted) {
										Log("Gap too large (after 0): " + i.ToString() + " (Gap: " + timeGap + ")");
										_failed = true;
										//AddBit(1);
									}
								}
							}
						}
					} catch(Exception ex) {
						//An exception occurred (too small/large gap, or invalid 0 bit + 8 data bits pattern)
						Log("Failed to process page: " + ex.ToString());
						_failed = true;
					}

					if(!_dataStarted) {
						_lastBit = 0;
					}
				}
			}

			SavePage(0);

			if(_failedIndex.Count > 0) {
				System.Diagnostics.Debug.WriteLine("Failed " + _failedIndex.Count + " pages (" + (_failedIndex.Count * 100 / _pages.Count) + "%)");
			}
			
			Directory.CreateDirectory("output");

			if(!_allPagesValid) {
				File.WriteAllText(Path.Combine("output", waveName + "_errors.txt"), _errorLog.ToString());
				return;
			}

			using(BinaryWriter writer = new BinaryWriter(File.Open(Path.Combine("output", waveName + ".studybox"), FileMode.Create), Encoding.UTF8)) {
				writer.Write(Encoding.UTF8.GetBytes("STBX"));
				writer.Write(4); //Chunk Length
				writer.Write(0x100); //Version 1.00

				StringBuilder sb = new StringBuilder();
				for(int i = 0; i < _pages.Count; i++) {
					PageInfo page = _pages[i];
					
					sb.AppendLine("Page index #" + i);
					sb.AppendLine("Internal page ID: " + page.Data[5].ToString());
					sb.AppendLine("Lead-in start position: " + page.LeadInPosition);
					sb.AppendLine("Lead-in size: " + (page.StartPosition - page.LeadInPosition));
					sb.AppendLine("Data start position: " + page.StartPosition);
					sb.AppendLine("---------------------------");
					sb.AppendLine("");

					writer.Write(Encoding.UTF8.GetBytes("PAGE"));
					writer.Write(page.Data.Count + 8); //Chunk length
					writer.Write(page.LeadInPosition); //Start position for this data track's lead in sequence (in samples)
					writer.Write(page.StartPosition); //Start position for this data track (in samples) (first bit of data)
					writer.Write(page.Data.ToArray()); //Page data
				}
				File.WriteAllText(Path.Combine("output", waveName + "-pageinfo.txt"), sb.ToString());

				byte[] waveFile = File.ReadAllBytes(_wavFile);
				List<byte> wavData = new List<byte>();
				for(int i = 0; i < 44; i++) {
					wavData.Add(waveFile[i]);
				}

				uint dataSize = (uint)(wavData[0x28] | (wavData[0x29] << 8) | (wavData[0x2A] << 16) | (wavData[0x2B] << 24));
				dataSize /= 2;
				wavData[0x28] = (byte)(dataSize & 0xFF);
				wavData[0x29] = (byte)((dataSize >> 8) & 0xFF);
				wavData[0x2A] = (byte)((dataSize >> 16) & 0xFF);
				wavData[0x2B] = (byte)((dataSize >> 24) & 0xFF);
				for(int i = 44; i < waveFile.Length; i += 4) {
					wavData.Add(waveFile[i]);
					wavData.Add(waveFile[i+1]);
				}

				//nChannels = 1
				wavData[0x16] = 1; //Make this a mono file

				//nAvgBytesPerSec = 88200 bytes / second
				wavData[0x1C] = 0x88;
				wavData[0x1D] = 0x58;
				wavData[0x1E] = 0x01;
				wavData[0x1F] = 0x00;

				//nBlockAlign = 2 bytes per block (align)
				wavData[0x20] = 0x02;
				wavData[0x21] = 0x00;

				int riffChunkSize = wavData.Count - 8;
				wavData[4] = (byte)(riffChunkSize & 0xFF);
				wavData[5] = (byte)((riffChunkSize >> 8) & 0xFF);
				wavData[6] = (byte)((riffChunkSize >> 16) & 0xFF);
				wavData[7] = (byte)((riffChunkSize >> 24) & 0xFF);

				writer.Write(Encoding.UTF8.GetBytes("AUDI"));
				writer.Write(wavData.Count + 4); //Chunk length
				writer.Write(0); //Audio file type ($0 = WAV)
				writer.Write(wavData.ToArray()); //Embedded audio file
			}
		}
	}

	struct PageInfo
	{
		public string FileName;
		public int StartPosition;
		public int LeadInPosition;
		public List<byte> Data;
	}
}
