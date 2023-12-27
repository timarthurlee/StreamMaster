﻿using StreamMaster.Application.M3UFiles;
using StreamMaster.Application.M3UFiles.Commands;
using StreamMaster.Domain.Enums;

namespace StreamMaster.Infrastructure.Services;

public partial class BackgroundTaskQueue : IM3UFileTasks
{
    public async ValueTask ProcessM3UFile(int Id, bool immediate = false, CancellationToken cancellationToken = default)
    {
        if (immediate)
        {
            _ = await _sender.Send(new ProcessM3UFileRequest(Id), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await QueueAsync(SMQueCommand.ProcessM3UFile, Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask ProcessM3UFiles(CancellationToken cancellationToken = default)
    {
        await QueueAsync(SMQueCommand.ProcessM3UFiles, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask ScanDirectoryForM3UFiles(CancellationToken cancellationToken = default)
    {
        await QueueAsync(SMQueCommand.ScanDirectoryForM3UFiles, cancellationToken).ConfigureAwait(false);
    }
}
