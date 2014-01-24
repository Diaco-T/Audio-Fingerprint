namespace SoundFingerprinting.Dao.SQL
{
    using System.Collections.Generic;
    using System.Text;

    using SoundFingerprinting.Data;
    using SoundFingerprinting.Infrastructure;

    internal class HashBinDao : AbstractDao, IHashBinDao
    {
        private const string SpReadFingerprintsByHashBinHashTableAndThreshold = "sp_ReadFingerprintsByHashBinHashTableAndThreshold";

        private const string SpReadHashDataByTrackId = "sp_ReadHashDataByTrackId";

        public HashBinDao()
            : base(
                DependencyResolver.Current.Get<IDatabaseProviderFactory>(),
                DependencyResolver.Current.Get<IModelBinderFactory>())
        {
            // no op   
        }

        public HashBinDao(IDatabaseProviderFactory databaseProvider, IModelBinderFactory modelBinderFactory)
            : base(databaseProvider, modelBinderFactory)
        {
             // no op
        }

        public void Insert(long[] hashBins, long subFingerprintId, int trackId)
        {
            StringBuilder sqlToExecute = new StringBuilder();
            for (int i = 0; i < hashBins.Length; i++)
            {
                sqlToExecute.Append("INSERT INTO HashTable_" + (i + 1) + "(HashBin, SubFingerprintId, TrackId) VALUES(" + hashBins[i] + "," + subFingerprintId + "," + trackId + ");");
                if (hashBins.Length > i + 1)
                {
                    sqlToExecute.Append("\n\r");
                }
            }

            PrepareSQLText(sqlToExecute.ToString()).AsNonQuery();
        }

        public IList<HashBinData> ReadHashBinsByHashTable(int hashTableId)
        {
            string sqlToExecute = "SELECT * FROM HashTable_" + hashTableId;
            return PrepareSQLText(sqlToExecute).AsListOfComplexModel<HashBinData>(
                (item, reader) =>
                    {
                        long subFingerprintId = reader.GetInt64("SubFingerprintId");
                        item.SubFingerprintReference = new ModelReference<long>(subFingerprintId);
                        item.HashTable = hashTableId;
                    });
        }

        public IList<HashData> ReadHashDataByTrackId(int trackId)
        {
            const int HashTablesCount = 25;
            return PrepareStoredProcedure(SpReadHashDataByTrackId).WithParameter("TrackId", trackId)
                .Execute()
                .AsList(reader =>
                    {
                        byte[] signature = (byte[])reader.GetRaw("Signature");
                        long[] hashBins = new long[HashTablesCount];
                        for (int i = 1; i <= HashTablesCount; i++)
                        {
                            hashBins[i - 1] = reader.GetInt64("HashBin_" + i);
                        }

                        return new HashData(signature, hashBins);
                    });
        }

        public IEnumerable<SubFingerprintData> ReadSubFingerprintDataByHashBucketsWithThreshold(long[] hashBuckets, int thresholdVotes)
        {
            return PrepareStoredProcedure(SpReadFingerprintsByHashBinHashTableAndThreshold)
                    .WithParameter("HashBin_1", hashBuckets[0])
                    .WithParameter("HashBin_2", hashBuckets[1])
                    .WithParameter("HashBin_3", hashBuckets[2])
                    .WithParameter("HashBin_4", hashBuckets[3])
                    .WithParameter("HashBin_5", hashBuckets[4])
                    .WithParameter("HashBin_6", hashBuckets[5])
                    .WithParameter("HashBin_7", hashBuckets[6])
                    .WithParameter("HashBin_8", hashBuckets[7])
                    .WithParameter("HashBin_9", hashBuckets[8])
                    .WithParameter("HashBin_10", hashBuckets[9])
                    .WithParameter("HashBin_11", hashBuckets[10])
                    .WithParameter("HashBin_12", hashBuckets[11])
                    .WithParameter("HashBin_13", hashBuckets[12])
                    .WithParameter("HashBin_14", hashBuckets[13])
                    .WithParameter("HashBin_15", hashBuckets[14])
                    .WithParameter("HashBin_16", hashBuckets[15])
                    .WithParameter("HashBin_17", hashBuckets[16])
                    .WithParameter("HashBin_18", hashBuckets[17])
                    .WithParameter("HashBin_19", hashBuckets[18])
                    .WithParameter("HashBin_20", hashBuckets[19])
                    .WithParameter("HashBin_21", hashBuckets[20])
                    .WithParameter("HashBin_22", hashBuckets[21])
                    .WithParameter("HashBin_23", hashBuckets[22])
                    .WithParameter("HashBin_24", hashBuckets[23])
                    .WithParameter("HashBin_25", hashBuckets[24])
                    .WithParameter("Threshold", thresholdVotes)
                    .Execute()
                    .AsList(reader =>
                        {
                            long id = reader.GetInt64("Id");
                            byte[] signature = (byte[])reader.GetRaw("Signature");
                            int trackId = reader.GetInt32("TrackId");
                            return new SubFingerprintData(
                                signature, new ModelReference<long>(id), new ModelReference<int>(trackId));
                        });
        }
    }
}