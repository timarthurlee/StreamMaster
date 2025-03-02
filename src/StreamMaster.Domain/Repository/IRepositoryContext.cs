﻿using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;

namespace StreamMaster.Domain.Repository;

public interface IRepositoryContext
{
    Task BulkUpdateEntitiesAsync<TEntity>(
    List<TEntity> entities,
    int batchSize = 100,
    int maxDegreeOfParallelism = 4,
    CancellationToken cancellationToken = default
) where TEntity : class;

    IQueryable<TResult> SqlQueryRaw<TResult>([NotParameterized] string sql, params object[] parameters);

    Task<int> ExecuteSqlRawAsync(string sql, params object[] parameters);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    Task BulkUpdateAsync<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;

    DbSet<TEntity> Set<TEntity>() where TEntity : class;

    int SaveChanges();

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    int ExecuteSqlRaw(string sql, params object[] parameters);

    Task<int> ExecuteSqlRawAsync(string sql, CancellationToken cancellationToken = default);

    Task BulkDeleteAsyncEntities<TEntity>(IQueryable<TEntity> entities, CancellationToken cancellationToken = default) where TEntity : class;

    DbSet<ChannelGroup> ChannelGroups { get; set; }

    DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

    DbSet<EPGFile> EPGFiles { get; set; }

    DbSet<UserGroup> UserGroups { get; set; }

    DbSet<APIKey> APIKeys { get; set; }

    DbSet<Device> Devices { get; set; }

    DbSet<M3UGroup> M3UGroups { get; set; }

    DbSet<M3UFile> M3UFiles { get; set; }

    DbSet<StreamGroupChannelGroup> StreamGroupChannelGroups { get; set; }

    DbSet<StreamGroupSMChannelLink> StreamGroupSMChannelLinks { get; set; }

    DbSet<SMChannelChannelLink> SMChannelChannelLinks { get; set; }

    DbSet<SMChannelStreamLink> SMChannelStreamLinks { get; set; }

    DbSet<StreamGroupProfile> StreamGroupProfiles { get; set; }

    DbSet<StreamGroup> StreamGroups { get; set; }

    DbSet<SMStream> SMStreams { get; set; }

    DbSet<SMChannel> SMChannels { get; set; }

    DbSet<SystemKeyValue> SystemKeyValues { get; set; }

    ChangeTracker ChangeTracker { get; }

    void Dispose();

    bool IsEntityTracked<TEntity>(TEntity entity) where TEntity : class;
}