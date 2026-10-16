using DnaEntropyGraph.Core.Inputs;
using Shouldly;
using Xunit;

namespace DnaEntropyGraph.Core.Tests;

/// <summary>Issue #686 / Hard Rule 14: a paired GFF3 is copied under app data like the input; the user's file is never touched.</summary>
public sealed class LocalRunInputStoreAnnotationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deg-annot-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string UserFile(string name, string content)
    {
        var dir = Path.Combine(_root, "user");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task The_annotation_copy_is_flagged_and_kept_out_of_the_input_folder()
    {
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));
        var gff = UserFile("genes.gff3", "chr1\ts\tgene\t1\t3\t.\t+\t.\tID=a\n");

        var staged = await store.StageAnnotationAsync("job-1", gff, CancellationToken.None);

        staged.IsAnnotation.ShouldBeTrue();
        staged.FileName.ShouldBe("genes.gff3");
        staged.ObjectName.ShouldBe("input/genes.gff3");
        staged.LocalPath.ShouldBe(Path.Combine(_root, "appdata", "runs", "job-1", "annotation", "genes.gff3"));
        File.ReadAllBytes(staged.LocalPath).ShouldBe(File.ReadAllBytes(gff));
    }

    [Fact]
    public async Task Finding_the_staged_input_never_returns_the_annotation_whatever_the_names_sort_like()
    {
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));
        await store.StageAsync("job-1", UserFile("zzz.fasta", ">x\nACGT\n"), CancellationToken.None);
        await store.StageAnnotationAsync("job-1", UserFile("aaa.gff3", "x"), CancellationToken.None);

        (await store.FindAsync("job-1", CancellationToken.None))!.FileName.ShouldBe("zzz.fasta");
        Path.GetFileName(store.TryFindStagedInput("job-1")).ShouldBe("zzz.fasta");
    }

    [Fact]
    public async Task The_source_is_never_modified_and_nothing_is_written_next_to_it()
    {
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));
        var gff = UserFile("genes.gff3", "x");
        var before = (File.ReadAllText(gff), File.GetLastWriteTimeUtc(gff));

        await store.StageAnnotationAsync("job-1", gff, CancellationToken.None);

        (File.ReadAllText(gff), File.GetLastWriteTimeUtc(gff)).ShouldBe(before);
        Directory.GetFileSystemEntries(Path.GetDirectoryName(gff)!).ShouldBe([gff]);
    }

    [Fact]
    public async Task A_missing_annotation_throws_file_not_found_and_a_job_id_with_a_separator_is_refused()
    {
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));

        await Should.ThrowAsync<FileNotFoundException>(() => store.StageAnnotationAsync("job-1", Path.Combine(_root, "nope.gff3"), CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => store.StageAnnotationAsync("..\\x", UserFile("g.gff3", "x"), CancellationToken.None));
    }

    [Fact]
    public async Task FindAnnotation_returns_the_staged_copy_or_null()
    {
        var store = new LocalRunInputStore(Path.Combine(_root, "appdata"));
        (await store.FindAnnotationAsync("job-1", CancellationToken.None)).ShouldBeNull();

        await store.StageAnnotationAsync("job-1", UserFile("genes.gff3", "x"), CancellationToken.None);

        (await store.FindAnnotationAsync("job-1", CancellationToken.None))!.FileName.ShouldBe("genes.gff3");
    }
}
