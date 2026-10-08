// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard FLASH-UPDATE event flow derived from penumbra-main, Shomy 2026.
using System.Xml.Linq;
using GeekFlashCore.Protocol.Abstractions;

namespace GeekFlashCore.Protocol.Mtk.Da;

internal sealed partial class XmlSession
{
    private static string BundleName(string path)
    {
        path = path.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        if (path.Length is < 1 or > 1024 || path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl) || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new MtkResourceException("scatter device file path");
        return path;
    }

    public void FlashUpdate(Stream scatter, long scatterLength, IReadOnlyDictionary<string, (Stream Source, long Length)> images, IMtkScatterBackupStore backups, long maximumBackup, IProgress<ProgressRecord>? progress)
    {
        long total = images.Values.Aggregate(0L, (n, f) => checked(n + f.Length)), done = 0, uploadedTotal = 0;
        long backupBudget = maximumBackup > (long.MaxValue - 1168) / 2 ? long.MaxValue : maximumBackup * 2 + 1168;
        var persisted = new Dictionary<string, long>(StringComparer.Ordinal);
        var downloaded = new HashSet<string>(StringComparer.Ordinal);
        Begin(MtkXmlCommand.FlashUpdate, Args(("source_file", "./scatter.xml"), ("path_separator", "/"), ("backup_folder", ".")));
        XElement initial = ReceiveXml();
        Require(MtkXmlCommand.DownloadFile, initial);
        var initialPath = initial.Descendants("source_file").ToArray();
        if (initialPath.Length > 1 || initialPath.Length == 1 && BundleName(initialPath[0].Value) != "scatter.xml")
            throw new MtkResourceException("scatter manifest request");
        Download(scatterLength, scatter, initial);
        progress?.Report(new(total, 0, Strings.ScatterProgress) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Started });
        bool recordReceived = false;
        for (int i = 0; i < options.MaximumMessages; i++)
        {
            wire.Check();
            XElement request = ReceiveXml();
            string command = MtkXmlCodec.Value(request, "command");
            switch (command)
            {
                case MtkXmlCommand.Prefix + MtkXmlCommand.FileSysOperation:
                {
                    Require(MtkXmlCommand.FileSysOperation, request);
                    string key = MtkXmlCodec.Value(request, "key"), path = MtkXmlCodec.Value(request, "file_path");
                    // These DA operations address a virtual namespace. Never create/delete host paths or destroy backups.
                    if (key is "MKDIR" or "REMOVE-ALL" && path is "." or "./")
                    {
                        Ack();
                        Ack(key);
                        break;
                    }

                    string name = BundleName(path);
                    long? size = name == "scatter.xml" ? scatterLength : images.TryGetValue(name, out var file) ? file.Length : persisted.TryGetValue(name, out var stored) ? stored : null;
                    if (key == "REMOVE" && size.HasValue)
                    {
                        Ack();
                        Ack("REMOVE");
                        break;
                    }

                    if (key is not ("FILE-SIZE" or "FILE-EXIST" or "FILE-EXISTS" or "NOT-EXISTS"))
                        throw wire.Failure();
                    if (key == "FILE-SIZE" && size is null)
                        throw new MtkResourceException("scatter unregistered file");
                    Ack();
                    Ack(key != "FILE-SIZE" ? (size.HasValue ? "EXISTS" : "NOT-EXISTS") : "0x" + size!.Value.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                }

                case MtkXmlCommand.Prefix + MtkXmlCommand.DownloadFile:
                {
                    string name = BundleName(MtkXmlCodec.Value(request, "source_file"));
                    bool first = downloaded.Add(name);
                    if (images.TryGetValue(name, out var file))
                    {
                        file.Source.Position = 0;
                        Download(file.Length, file.Source, request);
                        if (first)
                            done = checked(done + file.Length);
                        progress?.Report(new(total, done, Strings.ScatterProgress) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Running });
                    }
                    else if (persisted.TryGetValue(name, out var length))
                    {
                        var data = backups.OpenRead(name);
                        if (data.Length != length)
                            throw new MtkResourceException("scatter persisted backup length");
                        using Stream input = data.OpenStream();
                        Download(length, input, request);
                    }
                    else
                        throw new MtkResourceException("scatter unregistered file");
                    break;
                }

                case MtkXmlCommand.Prefix + MtkXmlCommand.UploadFile:
                {
                    string name = BundleName(MtkXmlCodec.Value(request, "target_file"));
                    if (name.Contains('/') || name == "scatter.xml" || images.ContainsKey(name) || persisted.ContainsKey(name))
                        throw new MtkResourceException("scatter backup name");
                    var infos = request.Descendants("info").ToArray();
                    if (infos.Length > 1 || infos.Any(e => e.HasElements))
                        throw wire.Failure();
                    bool record = infos.Length == 1 && infos[0].Value == "record-file";
                    if (record && recordReceived)
                        throw new MtkResourceException("scatter repeated record");
                    long length;
                    using (Stream output = backups.Create(name))
                    {
                        if (!output.CanWrite)
                            throw new MtkResourceException("scatter backup stream");
                        if (record)
                        {
                            using var metadata = new MemoryStream();
                            length = Upload(metadata, 1168, 1168, request, () =>
                            {
                                ValidateProtectedRecord(metadata.GetBuffer().AsSpan(0, 1168));
                                output.Write(metadata.GetBuffer().AsSpan(0, 1168));
                                FlushBackup(output);
                            });
                            recordReceived = true;
                        }
                        else
                            length = Upload(output, null, Math.Min(maximumBackup, backupBudget - uploadedTotal), request, () => FlushBackup(output));
                    }

                    uploadedTotal = checked(uploadedTotal + length);
                    if (uploadedTotal > backupBudget)
                        throw new MtkResourceException("scatter backup aggregate");
                    persisted.Add(name, length);
                    break;
                }

                case MtkXmlCommand.Prefix + MtkXmlCommand.ProgressReport:
                    Progress(request);
                    break;
                case MtkXmlCommand.Prefix + MtkXmlCommand.End:
                    Require(MtkXmlCommand.End, request);
                    if (images.Keys.Any(name => !downloaded.Contains(name)))
                        throw new MtkResourceException("scatter incomplete downloads");
                    Ack();
                    progress?.Report(new(total, total, Strings.ScatterProgress) { Unit = ProgressUnit.Bytes, Phase = ProgressPhase.Completed });
                    return;
                default:
                    throw wire.Failure();
            }
        }

        throw wire.Failure();
    }

    private static void FlushBackup(Stream output)
    {
        if (output is FileStream file)
            file.Flush(true);
        else
            output.Flush();
    }

    private static void ValidateProtectedRecord(ReadOnlySpan<byte> data)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > 16 || BinaryPrimitives.ReadUInt32LittleEndian(data[1156..]) > 3 || BinaryPrimitives.ReadUInt32LittleEndian(data[1160..]) > 2 || BinaryPrimitives.ReadUInt32LittleEndian(data[1164..]) > 2)
            throw new MtkResourceException("scatter protected record");
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var section = data.Slice(4 + i * 72, 72);
            int end = section[..64].IndexOf((byte)0);
            if (end < 0)
                end = 64;
            if (end == 0 || section[..end].ContainsAnyExceptInRange((byte)0x21, (byte)0x7e) || BinaryPrimitives.ReadUInt32LittleEndian(section[64..]) > 2)
                throw new MtkResourceException("scatter protected section");
            string name = System.Text.Encoding.ASCII.GetString(section[..end]);
            if (!names.Add(name))
                throw new MtkResourceException("scatter duplicate protected section");
        }
    }
}
