/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.IO;
using System.Text;
using Xunit;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Tests;

public sealed class ChangeEvaluatorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));

    public ChangeEvaluatorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (IOException) { /* a scratch folder; the next run uses another */ }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private string WriteBytes(string name, byte[] content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task A_file_that_differs_from_its_baseline_is_modified_with_counts()
    {
        var before = Write("before.txt", "one\ntwo\n");
        var file = Write("file.txt", "one\nTWO\nthree\n");

        var result = await ChangeEvaluator.EvaluateAsync(file, BaselineRef.Cli(before));

        Assert.False(result.Unchanged);
        Assert.Equal(ChangeState.Modified, result.State);
        Assert.Equal(2, result.Added);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(CountsKind.Exact, result.Counts);
    }

    [Fact]
    public async Task A_file_identical_to_its_baseline_is_not_a_change()
    {
        var before = Write("before.txt", "same\n");
        var file = Write("file.txt", "same\n");

        Assert.True((await ChangeEvaluator.EvaluateAsync(file, BaselineRef.Cli(before))).Unchanged);
    }

    [Fact]
    public async Task A_file_that_did_not_exist_is_added()
    {
        var file = Write("new.txt", "a\nb\nc\n");

        var result = await ChangeEvaluator.EvaluateAsync(file, BaselineRef.Empty);

        Assert.Equal(ChangeState.Added, result.State);
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task A_file_created_and_removed_again_is_not_a_change()
    {
        var gone = Path.Combine(_folder, "gone.txt");

        Assert.True((await ChangeEvaluator.EvaluateAsync(gone, BaselineRef.Empty)).Unchanged);
    }

    [Fact]
    public async Task A_file_that_was_there_and_is_gone_is_deleted()
    {
        var before = Write("before.txt", "a\nb\n");
        var gone = Path.Combine(_folder, "gone.txt");

        var result = await ChangeEvaluator.EvaluateAsync(gone, BaselineRef.Cli(before));

        Assert.Equal(ChangeState.Deleted, result.State);
        Assert.Equal(0, result.Added);
        Assert.Equal(2, result.Deleted);
    }

    [Fact]
    public async Task Without_a_baseline_the_file_is_listed_but_not_counted()
    {
        var file = Write("file.txt", "a\n");

        var result = await ChangeEvaluator.EvaluateAsync(file, BaselineRef.None);

        Assert.Equal(ChangeState.Modified, result.State);
        Assert.Equal(CountsKind.Unavailable, result.Counts);
        Assert.False(result.BaselineMissing);
    }

    [Fact]
    public async Task A_baseline_the_cli_pruned_is_reported_as_missing()
    {
        var file = Write("file.txt", "a\n");
        var pruned = Path.Combine(_folder, "pruned@v1");

        var result = await ChangeEvaluator.EvaluateAsync(file, BaselineRef.Cli(pruned));

        Assert.True(result.BaselineMissing);
        Assert.Equal(CountsKind.Unavailable, result.Counts);
    }

    [Fact]
    public async Task A_line_ending_change_is_modified_with_nothing_counted()
    {
        var before = Write("before.txt", "a\nb\n");
        var file = Write("file.txt", "a\r\nb\r\n");

        var result = await ChangeEvaluator.EvaluateAsync(file, BaselineRef.Cli(before));

        // The bytes differ, so it is a change, but no line did.
        Assert.False(result.Unchanged);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task A_byte_order_mark_is_not_part_of_the_first_line()
    {
        var before = WriteBytes("before.txt", Encoding.UTF8.GetBytes("a\nb\n"));
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("a\nb\n"));
        var file = WriteBytes("file.txt", withBom);

        var result = await ChangeEvaluator.EvaluateAsync(file, BaselineRef.Cli(before));

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task A_utf16_file_is_read_as_text()
    {
        var before = WriteBytes("before.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("a\nb\n")));
        var file = WriteBytes("file.txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("a\nc\n")));

        var result = await ChangeEvaluator.EvaluateAsync(file, BaselineRef.Cli(before));

        Assert.False(result.IsBinary);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public async Task Binary_content_is_modified_without_counts()
    {
        var before = WriteBytes("before.bin", new byte[] { 1, 2, 0, 3 });
        var file = WriteBytes("file.bin", new byte[] { 1, 2, 0, 4 });

        var result = await ChangeEvaluator.EvaluateAsync(file, BaselineRef.Cli(before));

        Assert.True(result.IsBinary);
        Assert.Equal(CountsKind.Unavailable, result.Counts);
        Assert.Equal(ChangeState.Modified, result.State);
    }

    [Fact]
    public async Task A_file_too_large_to_read_into_memory_is_not_counted_but_still_compared()
    {
        var big = new string('x', (int)ChangeEvaluator.MaxCountedBytes + 10);
        var before = Write("before.txt", big);
        var same = Write("same.txt", big);
        var different = Write("different.txt", big.Substring(0, big.Length - 1) + "y");

        Assert.True((await ChangeEvaluator.EvaluateAsync(same, BaselineRef.Cli(before))).Unchanged);

        var result = await ChangeEvaluator.EvaluateAsync(different, BaselineRef.Cli(before));
        Assert.False(result.Unchanged);
        Assert.Equal(CountsKind.Unavailable, result.Counts);
    }

    private const string Ukrainian =
        "Цей рядок написано в кодуванні Windows-1251, без позначки порядку байтів.\n" +
        "Другий рядок теж українською: чуєш їх, доцю, га? Кумедна ж ти, прощайся без ґольфів!\n" +
        "Третій рядок потрібен, щоб детектору вистачило літер для впевненого висновку.\n";

    private const string UkrainianAddedLine = "Четвертий рядок додано.\n";

    // The case that matters most and that the detector is least sure of (0.51): mostly ASCII, with a few comments.
    private const string UkrainianSource =
        "// Цей метод повертає кількість доданих рядків між двома версіями файлу.\n" +
        "public static int CountAdded(string before, string after)\n" +
        "{\n" +
        "    // Порожній файл не має жодного рядка, тому нічого не рахуємо.\n" +
        "    if (after.Length == 0) { return 0; }\n" +
        "    return LineDiff.Count(before, after).Added;\n" +
        "}\n";

    private const string French =
        "Voici un texte en français, enregistré en Windows-1252, sans marque d'ordre des octets.\n" +
        "Il était une fois un été très chaud où les élèves étaient déjà à l'école, même le dimanche.\n" +
        "Cette troisième phrase donne assez de lettres accentuées pour que la détection soit sûre.\n";

    [Fact]
    public void A_file_in_a_legacy_code_page_is_read_in_it_when_the_detector_is_sure()
    {
        var ukrainian = Encoding.GetEncoding(1251).GetBytes(Ukrainian);
        var french = Encoding.GetEncoding(1252).GetBytes(French);

        Assert.Equal(Ukrainian, ChangeEvaluator.DecodePair(null, ukrainian).After);
        Assert.Equal(French, ChangeEvaluator.DecodePair(french, null).Before);
    }

    [Fact]
    public void A_source_file_with_a_few_comments_in_a_legacy_code_page_is_read_in_it()
    {
        var bytes = Encoding.GetEncoding(1251).GetBytes(UkrainianSource);

        Assert.Equal(UkrainianSource, ChangeEvaluator.DecodePair(bytes, null).Before);
    }

    [Fact]
    public void The_two_sides_of_a_legacy_file_are_read_in_the_same_code_page()
    {
        var before = Encoding.GetEncoding(1251).GetBytes(Ukrainian);
        var after = Encoding.GetEncoding(1251).GetBytes(Ukrainian + UkrainianAddedLine);

        var (a, b) = ChangeEvaluator.DecodePair(before, after);

        Assert.Equal(Ukrainian, a);
        Assert.Equal(Ukrainian + UkrainianAddedLine, b);
    }

    [Fact]
    public void A_file_that_is_valid_utf8_never_asks_for_a_code_page()
    {
        var bytes = new UTF8Encoding(false).GetBytes(Ukrainian);

        Assert.Equal(Ukrainian, ChangeEvaluator.Decode(bytes, () => throw new InvalidOperationException("the detector was asked")));
    }

    [Fact]
    public void Bytes_the_detector_is_not_sure_of_are_read_as_utf8_with_replacements_as_before()
    {
        // One byte that is an é in Windows-1252 and nothing in UTF-8: there is too little to say what code page it is.
        var bytes = new byte[] { 0x61, 0xE9, 0x62 };

        Assert.Null(ChangeEvaluator.DetectLegacy(bytes));
        Assert.Equal("a�b", ChangeEvaluator.Decode(bytes, () => ChangeEvaluator.DetectLegacy(bytes)));
    }

    [Fact]
    public void Binary_content_is_still_not_text_whatever_the_detector_might_say()
    {
        Assert.Null(ChangeEvaluator.Decode(new byte[] { 0xE9, 0x00, 0xE9 }, () => Encoding.GetEncoding(1252)));
    }

    [Fact]
    public async Task The_counts_of_a_legacy_file_are_those_of_its_lines()
    {
        var before = WriteBytes("before.txt", Encoding.GetEncoding(1251).GetBytes(Ukrainian));
        var file = WriteBytes("file.txt", Encoding.GetEncoding(1251).GetBytes(Ukrainian + UkrainianAddedLine));

        var result = await ChangeEvaluator.EvaluateAsync(file, BaselineRef.Cli(before));

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Deleted);
        Assert.False(result.IsBinary);
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_listed_as_changed_with_no_numbers_rather_than_throwing()
    {
        var before = Write("before.txt", "one\n");
        var file = Write("file.txt", "one\nlocked\n");

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await ChangeEvaluator.EvaluateAsync(file, BaselineRef.Cli(before));

            Assert.False(result.Unchanged);
            Assert.Equal(ChangeState.Modified, result.State);
            Assert.Equal(CountsKind.Unavailable, result.Counts);
            // A stamp no real file has, so the next refresh measures it again.
            Assert.Equal(-1, result.Length);
        }
    }

    [Theory]
    [InlineData("plain text\r\nwith an accent: é\r\n", "")]
    [InlineData("plain text\n", "utf8")]
    [InlineData("plain text\n", "utf16le")]
    [InlineData("plain text\n", "utf16be")]
    public void Text_that_encodes_back_to_the_same_bytes_is_given_back_as_text(string text, string mark)
    {
        Encoding encoding = mark switch
        {
            "utf8" => new UTF8Encoding(true),
            "utf16le" => new UnicodeEncoding(false, true),
            "utf16be" => new UnicodeEncoding(true, true),
            _ => new UTF8Encoding(false),
        };
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text));

        Assert.Equal(text, ChangeEvaluator.DecodeStrict(bytes));
    }

    [Fact]
    public void Bytes_that_are_not_valid_text_are_not_given_back_as_text()
    {
        // Latin-1 "café": the lenient reader would swap the accent for U+FFFD and lose it.
        Assert.Null(ChangeEvaluator.DecodeStrict(new byte[] { 0x63, 0x61, 0x66, 0xE9 }));
        // A UTF-32 mark reads as UTF-16 with a stray NUL.
        Assert.Null(ChangeEvaluator.DecodeStrict(new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00 }));
        // Binary.
        Assert.Null(ChangeEvaluator.DecodeStrict(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01 }));
        // A UTF-16 text cut in the middle of a character.
        Assert.Null(ChangeEvaluator.DecodeStrict(new byte[] { 0xFF, 0xFE, 0x61 }));
        Assert.Equal("", ChangeEvaluator.DecodeStrict(Array.Empty<byte>()));
    }

    [Fact]
    public void The_byte_order_mark_is_told_by_its_first_bytes()
    {
        Assert.Equal(ChangeEvaluator.Mark.None, ChangeEvaluator.ByteOrderMark(new byte[] { 0x61, 0x62 }));
        Assert.Equal(ChangeEvaluator.Mark.Utf8, ChangeEvaluator.ByteOrderMark(new byte[] { 0xEF, 0xBB, 0xBF, 0x61 }));
        Assert.Equal(ChangeEvaluator.Mark.Utf16LittleEndian, ChangeEvaluator.ByteOrderMark(new byte[] { 0xFF, 0xFE, 0x61, 0x00 }));
        Assert.Equal(ChangeEvaluator.Mark.Utf16BigEndian, ChangeEvaluator.ByteOrderMark(new byte[] { 0xFE, 0xFF, 0x00, 0x61 }));
        Assert.Equal(ChangeEvaluator.Mark.Utf32LittleEndian, ChangeEvaluator.ByteOrderMark(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }));
        Assert.Equal(ChangeEvaluator.Mark.None, ChangeEvaluator.ByteOrderMark(Array.Empty<byte>()));
    }
}

internal static class ByteArrayExtensions
{
    public static byte[] Concat(this byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }
}
