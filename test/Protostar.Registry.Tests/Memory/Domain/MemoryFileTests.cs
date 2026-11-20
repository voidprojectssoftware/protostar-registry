using System.Text;
using Protostar.Registry.Api.Memory;
using Protostar.Registry.Api.Skills;

namespace Protostar.Registry.Tests.Memory.Domain;

public sealed class MemoryFileTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly Guid Laptop = Guid.NewGuid();
    private static readonly Guid Desktop = Guid.NewGuid();
    private const string Repo = "/home/me/src/repo";

    private static MemoryFile NewFile() => MemoryFile.Create(Guid.NewGuid(), Guid.NewGuid(), RelativePath.FromTrusted("MEMORY.md"), Now);

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void The_first_record_creates_version_1()
    {
        var file = NewFile();

        var recorded = file.Record(Laptop, Repo, Text("a"), Now);

        Assert.Equal(new MemoryFileRecorded(MemoryFileChange.Created, 1), recorded);
        Assert.Single(file.Versions);
    }

    [Fact]
    public void Recording_the_same_content_again_stores_nothing_new()
    {
        var file = NewFile();
        file.Record(Laptop, Repo, Text("a"), Now);

        var recorded = file.Record(Laptop, Repo, Text("a"), Now);

        Assert.Equal(new MemoryFileRecorded(MemoryFileChange.Unchanged, 1), recorded);
        Assert.Single(file.Versions);
    }

    [Fact]
    public void Changed_content_becomes_the_next_version_and_the_earlier_one_is_kept()
    {
        var file = NewFile();
        file.Record(Laptop, Repo, Text("a"), Now);

        var recorded = file.Record(Laptop, Repo, Text("b"), Now);

        Assert.Equal(new MemoryFileRecorded(MemoryFileChange.Versioned, 2), recorded);
        Assert.Equal([1, 2], file.Versions.Select(v => v.Number).Order());
        Assert.Equal(Text("a"), file.Versions.Single(v => v.Number == 1).Content);
    }

    [Fact]
    public void Content_equal_to_an_older_version_points_at_it_instead_of_storing_it_again()
    {
        var file = NewFile();
        file.Record(Laptop, Repo, Text("a"), Now);
        file.Record(Laptop, Repo, Text("b"), Now);

        var recorded = file.Record(Laptop, Repo, Text("a"), Now);

        Assert.Equal(new MemoryFileRecorded(MemoryFileChange.Unchanged, 1), recorded);
        Assert.Equal(2, file.Versions.Count);
        Assert.Equal(1, file.Copies.Single().VersionNumber);
    }

    [Fact]
    public void Each_machine_and_folder_has_its_own_copy()
    {
        var file = NewFile();
        file.Record(Laptop, Repo, Text("a"), Now);
        file.Record(Desktop, Repo, Text("a"), Now);
        file.Record(Laptop, "/home/me/src/repo-clone", Text("a"), Now);

        Assert.Equal(3, file.Copies.Count);
        Assert.Single(file.Versions);
    }

    [Fact]
    public void A_version_records_the_machine_that_first_sent_it_and_its_hash()
    {
        var file = NewFile();
        file.Record(Laptop, Repo, Text("a"), Now);
        file.Record(Desktop, Repo, Text("a"), Now);

        var version = file.Versions.Single();
        Assert.Equal(Laptop, version.MachineId);
        Assert.Equal(Sha256Hash.Of(Text("a")), version.Sha256);
        Assert.Equal(1, version.Size);
    }

    [Fact]
    public void Marking_deleted_keeps_the_file_and_its_versions()
    {
        var file = NewFile();
        file.Record(Laptop, Repo, Text("a"), Now);

        var recorded = file.MarkDeleted(Laptop, Repo, Now.AddDays(1));

        Assert.Equal(new MemoryFileRecorded(MemoryFileChange.MarkedDeleted, 1), recorded);
        Assert.Single(file.Versions);
        Assert.Equal(Now.AddDays(1), file.Copies.Single().DeletedAt);
    }

    [Fact]
    public void Marking_deleted_again_changes_nothing()
    {
        var file = NewFile();
        file.Record(Laptop, Repo, Text("a"), Now);
        file.MarkDeleted(Laptop, Repo, Now.AddDays(1));

        var recorded = file.MarkDeleted(Laptop, Repo, Now.AddDays(2));

        Assert.Equal(MemoryFileChange.Unchanged, recorded.Change);
        Assert.Equal(Now.AddDays(1), file.Copies.Single().DeletedAt);
    }

    [Fact]
    public void Marking_deleted_on_a_copy_that_never_sent_the_file_is_unknown()
    {
        var file = NewFile();
        file.Record(Laptop, Repo, Text("a"), Now);

        var recorded = file.MarkDeleted(Desktop, Repo, Now);

        Assert.Equal(new MemoryFileRecorded(MemoryFileChange.Unknown, null), recorded);
        Assert.Null(file.Copies.Single().DeletedAt);
    }

    [Fact]
    public void Deleting_on_one_machine_leaves_the_other_machines_copy_present()
    {
        var file = NewFile();
        file.Record(Laptop, Repo, Text("a"), Now);
        file.Record(Desktop, Repo, Text("a"), Now);

        file.MarkDeleted(Laptop, Repo, Now);

        Assert.Null(file.Copies.Single(c => c.MachineId == Desktop).DeletedAt);
    }

    [Fact]
    public void A_deleted_copy_sent_again_is_present_again()
    {
        var file = NewFile();
        file.Record(Laptop, Repo, Text("a"), Now);
        file.MarkDeleted(Laptop, Repo, Now);

        var recorded = file.Record(Laptop, Repo, Text("a"), Now);

        Assert.Equal(MemoryFileChange.Unchanged, recorded.Change);
        Assert.Null(file.Copies.Single().DeletedAt);
    }

    [Fact]
    public void Content_over_the_size_limit_is_refused()
    {
        var file = NewFile();

        Assert.Throws<MemoryFileTooLargeException>(() => file.Record(Laptop, Repo, new byte[MemoryFileSizePolicy.MaxBytes + 1], Now));
        Assert.Empty(file.Versions);
    }

    [Fact]
    public void Content_at_the_size_limit_is_kept()
    {
        var file = NewFile();

        var recorded = file.Record(Laptop, Repo, new byte[MemoryFileSizePolicy.MaxBytes], Now);

        Assert.Equal(MemoryFileChange.Created, recorded.Change);
    }

    [Fact]
    public void The_stored_content_does_not_change_when_the_caller_reuses_its_buffer()
    {
        var file = NewFile();
        var buffer = Text("a");
        file.Record(Laptop, Repo, buffer, Now);

        buffer[0] = (byte)'z';

        Assert.Equal(Text("a"), file.Versions.Single().Content);
    }
}
