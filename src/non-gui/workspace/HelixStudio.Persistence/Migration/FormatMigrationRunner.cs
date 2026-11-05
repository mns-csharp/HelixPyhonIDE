namespace HelixPythonNamespace.Persistence.Migration
{
    using System;
    using System.Collections.Generic;
    using HelixPythonNamespace.Persistence.Dto;

    internal sealed class FormatMigrationRunner
    {
        private readonly List<IFormatMigration> _migrations;

        public FormatMigrationRunner(IEnumerable<IFormatMigration> migrations)
        {
            _migrations = new List<IFormatMigration>(migrations);
            _migrations.Sort(CompareMigrationsByFromVersion);
        }

        public DiagramFileDto MigrateToLatest(DiagramFileDto dto, int targetVersion)
        {
            while (dto.FormatVersion < targetVersion)
            {
                int currentVersion = dto.FormatVersion;
                IFormatMigration migration = FindMigration(currentVersion);

                if (migration == null)
                {
                    throw new InvalidOperationException(
                        string.Format(
                            "No migration found from format version {0}. Cannot migrate to version {1}.",
                            currentVersion,
                            targetVersion));
                }

                dto = migration.Migrate(dto);
                dto.FormatVersion = migration.ToVersion;
            }

            return dto;
        }

        private IFormatMigration FindMigration(int fromVersion)
        {
            for (int i = 0; i < _migrations.Count; i++)
            {
                if (_migrations[i].FromVersion == fromVersion)
                {
                    return _migrations[i];
                }
            }

            return null;
        }

        private static int CompareMigrationsByFromVersion(IFormatMigration a, IFormatMigration b)
        {
            return a.FromVersion.CompareTo(b.FromVersion);
        }
    }
}
