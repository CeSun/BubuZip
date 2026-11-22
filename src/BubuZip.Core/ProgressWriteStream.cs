namespace BubuZip.Core;

/// <summary>A seekable output file that reports its current size after each write.</summary>
internal sealed class ProgressWriteStream(string path, Action checkpoint, Action<long> written)
    : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous)
{
    public override void Write(byte[] buffer, int offset, int count)
    {
        checkpoint();
        base.Write(buffer, offset, count);
        written(Length);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        checkpoint();
        base.Write(buffer);
        written(Length);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        checkpoint();
        await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        written(Length);
    }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        checkpoint();
        await base.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        written(Length);
    }
    public override void WriteByte(byte value)
    {
        checkpoint();
        base.WriteByte(value);
        written(Length);
    }
}
