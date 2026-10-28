/*
  Wings Notification Hub — optional monthly partitioning for high-volume history tables (SQL Server 2016 SP1+).
  Apply AFTER migrate.sql on a database with Enterprise/Developer edition or SQL Server 2016 SP1+ (all editions).
  NOT executed in CI: review sizes, filegroups and the sliding-window job for your environment first.

  Candidates (largest growth):  NotificationDeliveries (CreatedAt), DeliveryAttempts (StartedAt),
  DeliveryEvents (RecordedAt), UsageRecords (OccurredAt), AuditLogs (OccurredAt).
  The application never relies on partitioning; the RetentionWorker still deletes rows past each tenant's
  DataRetentionDays, so SWITCH-OUT of old partitions is an optimisation for very large tenants.
*/

CREATE PARTITION FUNCTION pf_wnh_month (datetime2)
AS RANGE RIGHT FOR VALUES ('2026-01-01','2026-02-01','2026-03-01','2026-04-01','2026-05-01','2026-06-01',
                           '2026-07-01','2026-08-01','2026-09-01','2026-10-01','2026-11-01','2026-12-01','2027-01-01');
GO
CREATE PARTITION SCHEME ps_wnh_month AS PARTITION pf_wnh_month ALL TO ([PRIMARY]);
GO

/* Example: DeliveryEvents. The clustered primary key must include the partitioning column, so the PK is rebuilt
   as a non-clustered key and a clustered index is created on the partition scheme. */
ALTER TABLE [DeliveryEvents] DROP CONSTRAINT [PK_DeliveryEvents];
GO
ALTER TABLE [DeliveryEvents] ADD CONSTRAINT [PK_DeliveryEvents] PRIMARY KEY NONCLUSTERED ([Id]) ON [PRIMARY];
GO
CREATE CLUSTERED INDEX [CX_DeliveryEvents_RecordedAt] ON [DeliveryEvents] ([RecordedAt], [Id]) ON ps_wnh_month([RecordedAt]);
GO

/* Repeat for DeliveryAttempts(StartedAt), UsageRecords(OccurredAt) and AuditLogs(OccurredAt).
   NotificationDeliveries is referenced by updates on Id: keep its clustered PK and partition only if a single
   tenant exceeds ~100M rows; otherwise rely on the (TenantId, CompanyId, CreatedAt) index and retention deletes. */

/* Monthly maintenance (SQL Agent job): add next month's boundary, then switch out / truncate the oldest partition.
   ALTER PARTITION SCHEME ps_wnh_month NEXT USED [PRIMARY];
   ALTER PARTITION FUNCTION pf_wnh_month() SPLIT RANGE ('2027-02-01');
   TRUNCATE TABLE [DeliveryEvents] WITH (PARTITIONS (1));
   ALTER PARTITION FUNCTION pf_wnh_month() MERGE RANGE ('2026-01-01');
*/
