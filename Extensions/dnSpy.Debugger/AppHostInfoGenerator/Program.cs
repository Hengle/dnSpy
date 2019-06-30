/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using dnSpy.Debugger.DotNet.CorDebug.Impl;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AppHostInfoGenerator {
	static class Program {
		// Add new versions from: https://www.nuget.org/packages/Microsoft.NETCore.DotNetAppHost/
		// The code ignores known versions so all versions can be added.
		//	^(\S+)\s.*		=>		\t\t\t"\1",
		static readonly string[] DotNetAppHost_Versions_ToCheck = new string[] {
			"3.0.0-preview6-27804-01",
			"3.0.0-preview5-27626-15",
			"3.0.0-preview4-27615-11",
			"3.0.0-preview3-27503-5",
			"3.0.0-preview-27324-5",
			"3.0.0-preview-27122-01",
			"2.2.5",
			"2.2.4",
			"2.2.3",
			"2.2.2",
			"2.2.1",
			"2.2.0",
			"2.2.0-preview3-27014-02",
			"2.2.0-preview2-26905-02",
			"2.2.0-preview-26820-02",
			"2.1.11",
			"2.1.10",
			"2.1.9",
			"2.1.8",
			"2.1.7",
			"2.1.6",
			"2.1.5",
			"2.1.4",
			"2.1.3",
			"2.1.2",
			"2.1.1",
			"2.1.0",
			"2.1.0-rc1",
			"2.1.0-preview2-26406-04",
			"2.1.0-preview1-26216-03",
			"2.0.9",
			"2.0.7",
			"2.0.6",
			"2.0.5",
			"2.0.4",
			"2.0.3",
			"2.0.0",
			"2.0.0-preview2-25407-01",
			"2.0.0-preview1-002111-00",
		};
		const string NuGetPackageDownloadUrlFormatString = "https://www.nuget.org/api/v2/package/{0}/{1}";
		static readonly byte[] appHostRelPathHash = Encoding.ASCII.GetBytes("c3ab8ff13720e8ad9047dd39466b3c89" + "74e592c2fa383d4a3960714caef0c4f2");
		const int HashSize = 0x2000;
		const int MinHashSize = 0x800;

		static int Main(string[] args) {
			try {
				var knownVersions = new HashSet<string>(AppHostInfoData.KnownAppHostInfos.Select(a => a.Version));
				var newInfos = new List<AppHostInfo>();
				var errors = new List<string>();
				foreach (var version in DotNetAppHost_Versions_ToCheck) {
					if (knownVersions.Contains(version))
						continue;

					Console.WriteLine();
					Console.WriteLine($"Runtime version: {version}");

					byte[] fileData;
					fileData = DownloadNuGetPackage("Microsoft.NETCore.DotNetAppHost", version);
					using (var zip = new ZipArchive(new MemoryStream(fileData), ZipArchiveMode.Read, leaveOpen: false)) {
						var runtimeJsonString = GetFileAsString(zip, "runtime.json");
						var runtimeJson = (JObject)JsonConvert.DeserializeObject(runtimeJsonString);
						foreach (JProperty runtime in runtimeJson["runtimes"]) {
							var runtimeName = runtime.Name;
							if (runtime.Count != 1)
								throw new InvalidOperationException("Expected 1 child");
							var dotNetAppHostObject = (JObject)runtime.First;
							if (dotNetAppHostObject.Count != 1)
								throw new InvalidOperationException("Expected 1 child");
							var dotNetAppHostObject2 = (JObject)dotNetAppHostObject["Microsoft.NETCore.DotNetAppHost"];
							if (dotNetAppHostObject2.Count != 1)
								throw new InvalidOperationException("Expected 1 child");
							var dotNetAppHostProperty = (JProperty)dotNetAppHostObject2.First;
							if (dotNetAppHostProperty.Count != 1)
								throw new InvalidOperationException("Expected 1 child");
							var runtimePackageName = dotNetAppHostProperty.Name;
							var runtimePackageVersion = (string)((JValue)dotNetAppHostProperty.Value).Value;
							Console.WriteLine();
							Console.WriteLine($"{runtimePackageName} {runtimePackageVersion}");
							if (!TryDownloadNuGetPackage(runtimePackageName, runtimePackageVersion, out var ridData)) {
								var error = $"***ERROR: 404 NOT FOUND: Couldn't download {runtimePackageName} = {runtimePackageVersion}";
								errors.Add(error);
								Console.WriteLine(error);
								continue;
							}
							using (var ridZip = new ZipArchive(new MemoryStream(ridData), ZipArchiveMode.Read, leaveOpen: false)) {
								var appHostEntries = GetAppHostEntries(ridZip).ToArray();
								if (appHostEntries.Length == 0)
									throw new InvalidOperationException("Expected at least one apphost");
								foreach (var info in appHostEntries) {
									if (info.rid != runtimeName)
										throw new InvalidOperationException($"Expected rid='{runtimeName}' but got '{info.rid}' from the zip file");
									var appHostData = GetData(info.entry);
									int relPathOffset = GetOffset(appHostData, appHostRelPathHash);
									if (relPathOffset < 0)
										throw new InvalidOperationException($"Couldn't get offset of hash in apphost: '{info.entry.FullName}'");
									var exeReader = new BinaryReader(new MemoryStream(appHostData));
									if (!ExeUtils.TryGetTextSectionInfo(exeReader, out var textOffset, out var textSize))
										throw new InvalidOperationException("Could not get .text offset/size");
									if (!TryHashData(appHostData, relPathOffset, textOffset, textSize, out var hashDataOffset, out var hashDataSize, out var hash))
										throw new InvalidOperationException("Failed to hash the .text section");
									newInfos.Add(new AppHostInfo(info.rid, runtimePackageVersion, (uint)relPathOffset, (uint)hashDataOffset, (uint)hashDataSize, hash));
								}
							}
						}
					}
				}

				if (newInfos.Count > 0) {
					Console.WriteLine();
					Console.WriteLine($"New apphost infos:");
					foreach (var info in newInfos)
						Serialize(info);
					Console.WriteLine($"{newInfos.Count} new infos");
					Console.WriteLine("********************************************************");
					Console.WriteLine($"*** UPDATE {nameof(AppHostInfoData)}.{nameof(AppHostInfoData.SerializedAppHostInfosCount)} from {AppHostInfoData.SerializedAppHostInfosCount} to {newInfos.Count + AppHostInfoData.SerializedAppHostInfosCount}");
					Console.WriteLine("********************************************************");
				}
				else
					Console.WriteLine("No new apphosts found");

				var hashes = new Dictionary<AppHostInfo, List<AppHostInfo>>(AppHostInfoEqualityComparer.Instance);
				foreach (var info in AppHostInfoData.KnownAppHostInfos.Concat(newInfos)) {
					if (!hashes.TryGetValue(info, out var list))
						hashes.Add(info, list = new List<AppHostInfo>());
					list.Add(info);
				}
				foreach (var kv in hashes) {
					var list = kv.Value;
					var info = list[0];
					bool bad = false;
					for (int i = 1; i < list.Count; i++) {
						// If all hash fields are the same, then we require that RelPathOffset also be
						// the same. If this is a problem, hash more data, or allow RelPathOffset to be
						// different (need to add code to verify the string at that location and try
						// the other offset if it's not a valid file).
						if (info.RelPathOffset != list[i].RelPathOffset) {
							bad = true;
							break;
						}
					}
					if (bad) {
						Console.WriteLine($"*** ERROR: The following apphosts have the same hash but different RelPathOffset:");
						foreach (var info2 in list)
							Console.WriteLine($"\t{info2.Rid} {info2.Version} RelPathOffset=0x{info2.RelPathOffset.ToString("X8")}");
					}
				}

				if (errors.Count > 0) {
					Console.WriteLine();
					Console.WriteLine("All download errors:");
					foreach (var error in errors)
						Console.WriteLine($"\t{error}");
				}

				return 0;
			}
			catch (Exception ex) {
				Console.WriteLine(ex.ToString());
				return 1;
			}
		}

		sealed class AppHostInfoEqualityComparer : IEqualityComparer<AppHostInfo> {
			public static readonly AppHostInfoEqualityComparer Instance = new AppHostInfoEqualityComparer();
			AppHostInfoEqualityComparer() { }

			public bool Equals(AppHostInfo x, AppHostInfo y) =>
				x.HashDataOffset == y.HashDataOffset &&
				x.HashDataSize == y.HashDataSize &&
				ByteArrayEquals(x.Hash, y.Hash);

			public int GetHashCode(AppHostInfo obj) =>
				(int)(obj.RelPathOffset ^ obj.HashDataOffset ^ obj.HashDataSize) ^ ByteArrayGetHashCode(obj.Hash);

			static bool ByteArrayEquals(byte[] a, byte[] b) {
				if (a.Length != b.Length)
					return false;
				for (int i = 0; i < a.Length; i++) {
					if (a[i] != b[i])
						return false;
				}
				return true;
			}

			// It's a sha1 hash, return the 1st 4 bytes
			static int ByteArrayGetHashCode(byte[] a) => BitConverter.ToInt32(a, 0);
		}

		static void Serialize(in AppHostInfo info) {
			Console.WriteLine();
			SerializeString(info.Rid, nameof(info.Rid));
			SerializeString(info.Version, nameof(info.Version));
			SerializeCompressedUInt32(info.RelPathOffset, nameof(info.RelPathOffset));
			SerializeCompressedUInt32(info.HashDataOffset, nameof(info.HashDataOffset));
			SerializeCompressedUInt32(info.HashDataSize, nameof(info.HashDataSize));
			SerializeByteArray(info.Hash, nameof(info.Hash), null, needLength: false);
		}
		const string serializeIndent = "\t\t\t";

		static void WriteComment(string name, string? origValue) {
			if (origValue is null)
				Console.Write($"// {name}");
			else
				Console.Write($"// {name} = {origValue}");
		}

		static void SerializeString(string value, string name) {
			var encoding = AppHostInfoData.StringEncoding;
			var data = encoding.GetBytes(value);
			if (encoding.GetString(data) != value)
				throw new InvalidOperationException();
			SerializeByteArray(data, name, value, needLength: true);
		}

		static void SerializeCompressedUInt32(uint value, string name) {
			Console.Write(serializeIndent);

			if (value <= 0x7F)
				Console.Write($"0x{((byte)value).ToString("X2")},");
			else if (value <= 0x3FFF)
				Console.Write($"0x{((byte)((value >> 8) | 0x80)).ToString("X2")}, 0x{((byte)value).ToString("X2")},");
			else if (value <= 0x1FFFFFFF)
				Console.Write($"0x{((byte)((value >> 24) | 0xC0)).ToString("X2")}, 0x{((byte)(value >> 16)).ToString("X2")}, 0x{((byte)(value >> 8)).ToString("X2")}, 0x{((byte)value).ToString("X2")},");
			else
				throw new InvalidOperationException();

			WriteComment(name, "0x" + value.ToString("X8"));
			Console.WriteLine();
		}

		static void SerializeByteArray(byte[] value, string name, string? origValue, bool needLength) {
			Console.Write(serializeIndent);
			if (value.Length > byte.MaxValue)
				throw new InvalidOperationException();

			bool needComma = false;
			if (needLength) {
				Console.Write("0x");
				Console.Write(value.Length.ToString("X2"));
				needComma = true;
			}
			for (int i = 0; i < value.Length; i++) {
				if (needComma)
					Console.Write(", ");
				Console.Write("0x");
				Console.Write(value[i].ToString("X2"));
				needComma = true;
			}
			Console.Write(',');

			WriteComment(name, origValue);
			Console.WriteLine();
		}

		static bool TryHashData(byte[] appHostData, int relPathOffset, int textOffset, int textSize, out int hashDataOffset, out int hashDataSize, [NotNullWhenTrue] out byte[]? hash) {
			hashDataOffset = textOffset;
			hashDataSize = Math.Min(textSize, HashSize);
			int hashDataSizeEnd = hashDataOffset + hashDataSize;
			int relPathOffsetEnd = relPathOffset + AppHostInfo.MaxAppHostRelPathLength;
			if ((hashDataOffset >= relPathOffsetEnd || hashDataSizeEnd <= relPathOffset) && hashDataSize >= MinHashSize) {
				using (var sha1 = new SHA1Managed())
					hash = sha1.ComputeHash(appHostData, hashDataOffset, hashDataSize);
				return true;
			}
			hash = null;
			return false;
		}

		static int GetOffset(byte[] bytes, byte[] pattern) {
			int si = 0;
			var b = pattern[0];
			while (si < bytes.Length) {
				si = Array.IndexOf(bytes, b, si);
				if (si < 0)
					break;
				if (Match(bytes, si, pattern))
					return si;
				si++;
			}
			return -1;
		}

		static bool Match(byte[] bytes, int index, byte[] pattern) {
			if (index + pattern.Length > bytes.Length)
				return false;
			for (int i = 0; i < pattern.Length; i++) {
				if (bytes[index + i] != pattern[i])
					return false;
			}
			return true;
		}

		const string runtimesDir = "runtimes";
		const string nativeDir = "native";
		static readonly HashSet<string> apphostNames = new HashSet<string> {
			"apphost",
			"apphost.exe",
		};
		static readonly HashSet<string> ignoredNames = new HashSet<string> {
			"comhost.dll",
			"ijwhost.dll",
			"ijwhost.lib",
			"libnethost.dylib",
			"libnethost.so",
			"nethost.dll",
			"nethost.h",
			"nethost.lib",
		};
		static IEnumerable<(ZipArchiveEntry entry, string rid)> GetAppHostEntries(ZipArchive zip) {
			foreach (var entry in zip.Entries) {
				var fullName = entry.FullName;
				if (!TryGetRid(fullName, out var rid, out var filename)) {
					if (fullName.StartsWith(runtimesDir + "/")) {
						Debug.Assert(false);
						throw new InvalidOperationException($"Unknown {runtimesDir} dir filename, not an apphost: '{filename}'");
					}
					continue;
				}
				if (ignoredNames.Contains(filename))
					continue;
				if (!apphostNames.Contains(filename)) {
					Debug.Assert(false);
					throw new InvalidOperationException($"Unknown apphost filename: '{filename}', fullName = '{fullName}'");
				}
				yield return (entry, rid);
			}
		}

		static bool TryGetRid(string fullName, [NotNullWhenTrue] out string? rid, [NotNullWhenTrue] out string? filename) {
			rid = null;
			filename = null;
			var parts = fullName.Split('/');
			if (parts.Length != 4)
				return false;
			if (parts[0] != runtimesDir)
				return false;
			if (parts[2] != nativeDir)
				return false;
			rid = parts[1];
			filename = parts[3];
			return true;
		}

		static byte[] DownloadNuGetPackage(string packageName, string version) {
			var url = string.Format(NuGetPackageDownloadUrlFormatString, packageName, version);
			Console.WriteLine($"Downloading {url}");
			using (var wc = new WebClient())
				return wc.DownloadData(url);
		}

		static bool TryDownloadNuGetPackage(string packageName, string version, [NotNullWhenTrue] out byte[]? data) {
			try {
				data = DownloadNuGetPackage(packageName, version);
				return true;
			}
			catch (WebException wex) when (wex.Response is HttpWebResponse responce && responce.StatusCode == HttpStatusCode.NotFound) {
				data = null;
				return false;
			}
		}

		static byte[] GetData(ZipArchive zip, string name) {
			var entry = zip.GetEntry(name);
			if (entry is null)
				throw new InvalidOperationException($"Couldn't find {name} in zip file");
			return GetData(entry);
		}

		static byte[] GetData(ZipArchiveEntry entry) {
			var data = new byte[entry.Length];
			using (var runtimeJsonStream = entry.Open()) {
				if (runtimeJsonStream.Read(data, 0, data.Length) != data.Length)
					throw new InvalidOperationException($"Could not read all bytes from compressed '{entry.FullName}'");
			}
			return data;
		}

		static string GetFileAsString(ZipArchive zip, string name) =>
			Encoding.UTF8.GetString(GetData(zip, name));
	}
}
