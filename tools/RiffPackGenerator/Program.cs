using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RiffPackGenerator;

public partial class Program {

	public const string ProjectNamePrefix = "RiffCollection";
	public const string ProjectNameSuffix = "BPM";

	public static void Main(string[] args) {

		if(args.Length != 2) {

			Console.Error.WriteLine("Two arguments must be specified (path to the projects directory, password)!");

			return;

		}

		string projectsDirectory = args[0];

		if(!Directory.Exists(projectsDirectory)) {

			Console.Error.WriteLine("Projects directory does not exist!");

			return;

		}

		List<Project> projects = [];

		Console.WriteLine($"Searching for projects in {projectsDirectory}...");

		foreach(string projectDirectory in
					Directory.GetDirectories(projectsDirectory, $"{ProjectNamePrefix}*")) {

			Console.WriteLine($"  Found {projectDirectory}...");

			string? projectName =
				Path.GetFileName(projectDirectory);

			if(projectName == null) {

				Console.Error.WriteLine($"    {nameof(Path)}.{nameof(Path.GetFileName)} failed!");

				continue;

			}

			Match bpmMatch = ProjectBPMMatch().Match(projectName);

			if(!bpmMatch.Success ||
				bpmMatch.Groups.Count != 2) {

				Console.Error.WriteLine("    Could not determine project BPM!");

				continue;

			}

			string bpmString =
				bpmMatch.Groups
						.Values
						.ElementAt(1)
						.Value;

			Console.WriteLine($"    Detected BPM: {bpmString}!");

			if(!int.TryParse(bpmString, out int bpm)) {

				Console.Error.WriteLine("    BPM is not a number!");

				continue;

			}

			string riffsDirectory = Path.Combine(projectDirectory, "Render");

			if(!Directory.Exists(riffsDirectory)) {

				Console.Error.WriteLine("    'Render' directory could not be found!");

				continue;

			}

			List<Riff> riffs = [];

			Console.WriteLine($"    Searching for riffs in: {riffsDirectory}...");

			foreach(string riffFile in
						Directory.GetFiles(riffsDirectory, "*.mp3")) {

				Console.WriteLine($"      Found {riffFile}...");

				string? riffName =
					Path.GetFileNameWithoutExtension(riffFile);

				if(riffName == null) {

					Console.Error.WriteLine($"      {nameof(Path)}.{nameof(Path.GetFileNameWithoutExtension)} failed!");

					continue;

				}

				Match riffDataMatch = RiffDataMatch().Match(riffName);

				if(!riffDataMatch.Success ||
					riffDataMatch.Groups.Count != 5) {

					Console.Error.WriteLine("      Could not parse riff data!");

					continue;

				}

				string indexString = riffDataMatch.Groups[2].Value;
				string noteString = riffDataMatch.Groups[3].Value;
				string statusString = riffDataMatch.Groups[4].Value;

				riffs.Add(new(int.Parse(indexString),
										noteString,
										string.IsNullOrWhiteSpace(statusString) ?
												RiffStatus.None :
												(RiffStatus) int.Parse(statusString),
										File.ReadAllBytes(riffFile)));

			}

			projects.Add(new(bpm, riffs));

		}

		Dictionary<string, Riff> riffMapping = [];

		Manifest manifest =
			new([

				..projects.SelectMany(project =>
												project.Riffs
														.Select(riff => {

															string id = Guid.NewGuid().ToString();

															riffMapping[id] = riff;

															return new ManifestRiffEntry(id,
																							project.BPM,
																							riff.Index,
																							riff.Note,
																							riff.Status);

														}))

			]);

		Console.WriteLine("Writing riff pack...");

		const int KeyLength = 256 / 8;

		ReadOnlySpan<byte> password = Encoding.UTF8.GetBytes(args[1]);
		ReadOnlySpan<byte> salt = "lis3a7u45yjhvnoliu7aswtnbvblwou7opna"u8;

		byte[] key;

		{

			Console.WriteLine("  Initializing AES...");

			using Aes aes = Aes.Create();

			Console.WriteLine($"    IV length: {aes.IV.Length} bytes");

			Console.WriteLine("    Generating key...");
			Console.WriteLine($"      Salt length: {salt.Length} bytes");
			aes.Key =
				key =
					Rfc2898DeriveBytes.Pbkdf2(password,
												salt,
												100_000,
												HashAlgorithmName.SHA512,
												KeyLength);
			Console.WriteLine($"      Generated {aes.Key.Length} bytes. Sample:");
			Console.WriteLine($"        {BytesToString([.. aes.Key.Take(8)])}");

			ICryptoTransform aesEncryptor =
				aes.CreateEncryptor();

			using FileStream riffPackFileStream =
				File.Create("../../../../../riff.pack");

			Console.WriteLine("  Writing data...");

			riffPackFileStream.Write(aes.IV);

			{

				using CryptoStream cryptoStream =
					new(riffPackFileStream,
									aesEncryptor,
									CryptoStreamMode.Write,
									true);

				using ZipArchive zipArchive =
					new(cryptoStream,
								ZipArchiveMode.Create);

				{

					ZipArchiveEntry manifestEntry = zipArchive.CreateEntry("manifest.json");

					using Stream stream = manifestEntry.Open();

					stream.Write(JsonSerializer.SerializeToUtf8Bytes(manifest));

					Console.WriteLine("    Wrote manifest...");

				}

				Console.WriteLine("    Writing riffs...");

				foreach(ManifestRiffEntry manifestRiffEntry in
							manifest.Riffs) {

					Riff riff = riffMapping[manifestRiffEntry.Id];

					ZipArchiveEntry riffEntry = zipArchive.CreateEntry(manifestRiffEntry.Id);

					using Stream stream = riffEntry.Open();

					stream.Write(riff.Bytes);

					Console.WriteLine($"      Wrote riff {manifestRiffEntry.BPM}/{riff.Index}...");

				}

			}

			riffPackFileStream.Flush();

			Console.WriteLine($"    Wrote {riffPackFileStream.Length} total bytes!");

			{

				int ivLength = aes.IV.Length;

				byte[] bytes = new byte[8];

				_ = riffPackFileStream.Seek(0, SeekOrigin.Begin);
				_ = riffPackFileStream.Read(bytes);

				Console.WriteLine($"    IV sample: {BytesToString(bytes)}");

				_ = riffPackFileStream.Seek(ivLength, SeekOrigin.Begin);
				_ = riffPackFileStream.Read(bytes);

				Console.WriteLine($"    Encrypted data sample: {BytesToString(bytes)}");

				_ = riffPackFileStream.Seek(0, SeekOrigin.End);

			}

		}

		Console.WriteLine("Writing decrypted riff pack...");

		{

			using FileStream riffPackFileStream =
				File.OpenRead("../../../../../riff.pack");

			byte[] iv = new byte[16];

			_ = riffPackFileStream.Read(iv, 0, iv.Length);

			Console.WriteLine($"  Read iv ({riffPackFileStream.Position} bytes)...");

			using Aes aes = Aes.Create();

			aes.IV = iv;
			aes.Key = key;

			ICryptoTransform aesDecryptor =
				aes.CreateDecryptor();

			using CryptoStream cryptoStream =
				new(riffPackFileStream,
								aesDecryptor,
								CryptoStreamMode.Read,
								true);

			using FileStream decryptedRiffPackFileStream =
				File.Create("../../../../../decrypted_riff_pack/riff.pack.zip");

			cryptoStream.CopyTo(decryptedRiffPackFileStream);

			Console.WriteLine($"  Wrote decrypted content ({decryptedRiffPackFileStream.Length} bytes)...");

			{

				byte[] bytes = new byte[8];

				_ = decryptedRiffPackFileStream.Seek(0, SeekOrigin.Begin);
				_ = decryptedRiffPackFileStream.Read(bytes);

				Console.WriteLine($"    Decrypted data sample: {BytesToString(bytes)}");

				_ = decryptedRiffPackFileStream.Seek(0, SeekOrigin.End);

			}

		}

	}

	public static string BytesToString(byte[] bytes) =>
		string.Join(' ',
					bytes.Select(b => $"{b:X2}"));

	public record Project(int BPM, List<Riff> Riffs);
	public record Riff(int Index,
						string Note,
						RiffStatus Status,
						byte[] Bytes);

	public record Manifest(List<ManifestRiffEntry> Riffs);
	public record ManifestRiffEntry(string Id,
									int BPM,
									int Index,
									string Note,
									RiffStatus Status);

	public enum RiffStatus {

		None,
		InUse,
		Rejected

	}

	[GeneratedRegex(@$"^{ProjectNamePrefix}(\d+){ProjectNameSuffix}$")]
	public static partial Regex ProjectBPMMatch();
	[GeneratedRegex(@"^(\d+)~(\d+)~(\S*)~(\d?)$")]
	public static partial Regex RiffDataMatch();

}