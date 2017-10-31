using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading;
using bladeDirectorWCF.Properties;

namespace bladeDirectorWCF
{
    public class hostDB : IDisposable
    {
        public SQLiteConnection conn;
        private readonly string dbFilename;

        public hostDB(string basePath)
        {
            // Juuuust to make sure
            string sqliteOpts = SQLiteConnection.SQLiteCompileOptions;
            if (!sqliteOpts.Contains("THREADSAFE=1"))
                throw new Exception("This build of SQLite is not threadsafe");

            dbFilename = Path.Combine(basePath, "hoststate.sqlite");

            // If we're making a new file, remember that, since we'll have to create a new schema.
            bool needToCreateSchema = !File.Exists(dbFilename);
            conn = new SQLiteConnection("Data Source=" + dbFilename);
            conn.Open();

            if (needToCreateSchema)
                createTables();
        }

        public hostDB()
        {
            dbFilename = ":memory:";

            conn = new SQLiteConnection("Data Source=" + dbFilename);
            conn.Open();

            createTables();
        }

        private void createTables()
        {
            string[] sqlCommands = Resources.DBCreation.Split(';');

            foreach (string sqlCommand in sqlCommands)
            {
                using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void dropDB()
        {
            if (conn != null)
            {
                conn.Close();
                conn.Dispose();
            }

            if (dbFilename != ":memory:")
            {
                DateTime deadline = DateTime.Now + TimeSpan.FromMinutes(1);
                while (true)
                {
                    try
                    {
                        File.Delete(dbFilename);
                        break;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (deadline < DateTime.Now)
                            throw;
                        Thread.Sleep(TimeSpan.FromSeconds(2));
                    }
                }
            }

            conn = new SQLiteConnection("Data Source=" + dbFilename);
            conn.Open();
        }

        public string[] getAllBladeIP()
        {
            string sqlCommand = "select bladeIP from bladeConfiguration";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    List<string> toRet = new List<string>();
                    while (reader.Read())
                        toRet.Add((string)reader[0]);
                    return toRet.ToArray();
                }
            }
        }

        public string[] getAllVMIP()
        {
            string sqlCommand = "select VMIP from VMConfiguration";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    List<string> toRet = new List<string>();
                    while (reader.Read())
                        toRet.Add((string)reader[0]);
                    return toRet.ToArray();
                }
            }
        }

        public disposingList<lockableBladeSpec> getAllBladeInfo(Func<bladeSpec, bool> filter, bladeLockType lockTypeRead, bladeLockType lockTypeWrite, bool permitAccessDuringBIOS = false, bool permitAccessDuringDeployment = false, int max = Int32.MaxValue)
        {
            disposingList<lockableBladeSpec> toRet = new disposingList<lockableBladeSpec>();
            foreach (string bladeIP in getAllBladeIP())
            {
                lockableBladeSpec blade = getBladeByIP(bladeIP, lockTypeRead, lockTypeWrite, true, true);
                // Filter out anything as requested
                if (!filter(blade.spec))
                {
                    blade.Dispose();
                    continue;
                }
                // Filter out anything we don't have access to right now, due to BIOS or VM deployments
                if ((!permitAccessDuringDeployment) &&
                    blade.spec.vmDeployState != VMDeployStatus.notBeingDeployed &&
                    blade.spec.vmDeployState != VMDeployStatus.failed &&
                    blade.spec.vmDeployState != VMDeployStatus.readyForDeployment)
                {
                    blade.Dispose();
                    continue;
                }
                if ((!permitAccessDuringBIOS) && blade.spec.currentlyHavingBIOSDeployed)
                {
                    blade.Dispose();
                    continue;
                }

                // Otherwise, okay.
                toRet.Add(blade);
            }
            return toRet;
        }

        public disposingList<lockableVMSpec> getAllVMInfo(Func<vmSpec, bool> filter, bladeLockType lockTypeRead, bladeLockType lockTypeWrite)
        {
            disposingList<lockableVMSpec> toRet = new disposingList<lockableVMSpec>();
            foreach (string bladeIP in getAllVMIP())
            {
                lockableVMSpec VM = getVMByIP(bladeIP, lockTypeRead, lockTypeWrite);
                if (filter(VM.spec))
                    toRet.Add(VM);
                else
                    VM.Dispose();
            }
            return toRet;
        }

        // FIXME: code duplication
        public lockableBladeSpec getBladeByIP(string IP, bladeLockType readLock, bladeLockType writeLock, bool permitAccessDuringBIOS = false, bool permitAccessDuringDeployment = false)
        {
            bladeLockType origReadLock = readLock | writeLock;
            readLock = origReadLock;

            // We need to lock IP addressess, since we're searching by them.
            readLock = readLock | bladeLockType.lockIPAddresses;
            readLock = readLock | bladeLockType.lockvmDeployState;
            readLock = readLock | bladeLockType.lockBIOS;

            lockableBladeSpec toRet = new lockableBladeSpec(conn, IP, readLock, writeLock);

            try
            {
                string sqlCommand = "select * from bladeOwnership " +
                                    "join bladeConfiguration on ownershipKey = bladeConfiguration.ownershipID " +
                                    "where bladeIP = $bladeIP";
                using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
                {
                    cmd.Parameters.AddWithValue("$bladeIP", IP);
                    using (SQLiteDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            bladeSpec newSpec = new bladeSpec(conn, reader, readLock, writeLock);
                            toRet.setSpec(newSpec);

                            if ((!permitAccessDuringDeployment) &&
                                newSpec.vmDeployState != VMDeployStatus.notBeingDeployed &&
                                newSpec.vmDeployState != VMDeployStatus.failed &&
                                newSpec.vmDeployState != VMDeployStatus.readyForDeployment)
                                throw new Exception("Attempt to access blade during VM deployment");
                            if ((!permitAccessDuringBIOS) && newSpec.currentlyHavingBIOSDeployed)
                                throw new Exception("Attempt to access blade during BIOS deployment");

                            if ((origReadLock & bladeLockType.lockvmDeployState) == 0 &&
                                (writeLock & bladeLockType.lockvmDeployState) == 0)
                                toRet.downgradeLocks(bladeLockType.lockvmDeployState, bladeLockType.lockNone);

                            if ((origReadLock & bladeLockType.lockBIOS) == 0 &&
                                (writeLock & bladeLockType.lockBIOS) == 0)
                                toRet.downgradeLocks(bladeLockType.lockBIOS, bladeLockType.lockNone);

                            return toRet;
                        }
                        // No records returned.
                        throw new bladeNotFoundException();
                    }
                }
            }
            catch (Exception)
            {
                toRet.Dispose();
                throw;
            }
        }

        // FIXME: code duplication ^^
        public bladeSpec getBladeByIP_withoutLocking(string IP)
        {
            string sqlCommand = "select * from bladeOwnership " +
                                "join bladeConfiguration on ownershipKey = bladeConfiguration.ownershipID " +
                                "where bladeIP = $bladeIP";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                cmd.Parameters.AddWithValue("$bladeIP", IP);
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new bladeSpec(conn, reader, bladeLockType.lockAll, bladeLockType.lockAll);
                    }
                    // No records returned.
                    throw new bladeNotFoundException();
                }
            }
        }

        public lockableVMSpec getVMByIP(string bladeName, bladeLockType readLock, bladeLockType writeLock)
        {
            // We need to lock IP addressess, since we're searching by them.
            readLock = readLock | bladeLockType.lockIPAddresses;

            string sqlCommand = "select * from bladeOwnership " +
                                "join VMConfiguration on ownershipKey = VMConfiguration.ownershipID " +
                                "where VMConfiguration.VMIP = $VMIP";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                cmd.Parameters.AddWithValue("$VMIP", bladeName);
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new lockableVMSpec(conn, reader, readLock, writeLock);
                    }
                    // No records returned.
                    return null;
                }
            }
        }

        // Fixme: code duplication ^^
        public vmSpec getVMByIP_withoutLocking(string bladeName)
        {
            string sqlCommand = "select * from bladeOwnership " +
                                "join VMConfiguration on ownershipKey = VMConfiguration.ownershipID " +
                                "where VMConfiguration.VMIP = $VMIP";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                cmd.Parameters.AddWithValue("$VMIP", bladeName);
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new vmSpec(conn, reader, bladeLockType.lockAll, bladeLockType.lockAll);
                    }
                    // No records returned.
                    return null;
                }
            }
        }

        public currentOwnerStat[] getFairnessStats_withoutLocking()
        {
            List<currentOwnerStat> blades = getFairnessStatsForBlades_withoutLocking().ToList();
            // Now add VM stats. 
            blades.RemoveAll(x => x.ownerName == "vmserver");

            foreach (string bladeIP in getAllBladeIP())
            {
                float totalVMs = 0;
                Dictionary<string, float> ownershipForThisBlade = new Dictionary<string, float>();

                string sqlCommand = "select bladeOwnership.currentOwner from VMConfiguration " +
                                    " join bladeOwnership on VMConfiguration.ownershipID = bladeOwnership.ownershipKey " +
                                    " where parentBladeIP == $bladeIP ";
                using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
                {
                    cmd.Parameters.AddWithValue("$bladeIP", bladeIP);
                    using (SQLiteDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string owner = reader[0].ToString();
                            if (!ownershipForThisBlade.ContainsKey(owner))
                                ownershipForThisBlade.Add(owner, 0);

                            ownershipForThisBlade[owner]++;

                            totalVMs++;
                        }
                    }
                }
                foreach (KeyValuePair<string, float> kvp in ownershipForThisBlade)
                {
                    if (blades.Count(x => x.ownerName == kvp.Key) == 0)
                        blades.Add(new currentOwnerStat(kvp.Key, 0));
                    blades.Single(x => x.ownerName == kvp.Key).allocatedBlades += ((1.0f/totalVMs) * kvp.Value);
                }
            }

            return blades.ToArray();
        }

        public currentOwnerStat[] getFairnessStatsForBlades_withoutLocking()
        {
            // TODO: check .state
            List<currentOwnerStat> toRet = new List<currentOwnerStat>();

            string sqlCommand = "select bladeOwnership.currentOwner, count(*) from bladeConfiguration " +
                                " join bladeOwnership on bladeConfiguration.ownershipID = bladeOwnership.ownershipKey " +
                                " group by bladeOwnership.currentOwner";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        toRet.Add(new currentOwnerStat(reader[0].ToString(), int.Parse(reader[1].ToString())));
                    }
                }
            }

            // Add any owners in the queue but not actually owning any blades
            string allOwnersSQL = "select bladeOwnership.currentOwner, bladeOwnership.nextOwner from bladeOwnership ";
            using (SQLiteCommand cmd = new SQLiteCommand(allOwnersSQL, conn))
            {
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (reader[0].ToString() != "" & toRet.Count(x => x.ownerName == reader[0].ToString()) == 0)
                            toRet.Add(new currentOwnerStat(reader[0].ToString(), 0));

                        if (reader[1].ToString() != "" & toRet.Count(x => x.ownerName == reader[1].ToString()) == 0)
                            toRet.Add(new currentOwnerStat(reader[1].ToString(), 0));
                    }
                }
            }


            return toRet.ToArray();
        }

        public lockableVMSpec getVMByDBID(long VMID)
        {
            return new lockableVMSpec(conn, getVMByDBID_nolocking(VMID));
        }

        public vmSpec getVMByDBID_nolocking(long VMID)
        {
            string sqlCommand = "select * from bladeOwnership " +
                                "join VMConfiguration on ownershipKey = VMConfiguration.ownershipID " +
                                "where vmConfigKey = $VMID";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                cmd.Parameters.AddWithValue("$VMID", VMID);
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new vmSpec(conn, reader, bladeLockType.lockAll, bladeLockType.lockAll);
                    }
                    // No records returned.
                    return null;
                }
            }
        }

        public disposingList<lockableVMSpec> getVMByVMServerIP(string vmServerIP)
        {
            List<vmSpec> VMs = getVMByVMServerIP_nolocking(vmServerIP);
            disposingList<lockableVMSpec> toRet = new disposingList<lockableVMSpec>();
            foreach (vmSpec vmSpec in VMs)
                toRet.Add(new lockableVMSpec(conn, vmSpec));
            return toRet;
        }

        public List<vmSpec> getVMByVMServerIP_nolocking(string vmServerIP)
        {
            List<long> VMIDs = new List<long>();
            string sqlCommand = "select vmConfigKey from vmConfiguration " +
                                "join bladeConfiguration on parentbladeID = bladeConfigKey " +
                                "where bladeIP = $vmServerIP";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                cmd.Parameters.AddWithValue("$vmServerIP", vmServerIP);
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        VMIDs.Add((long)reader["vmConfigKey"]);
                }
            }

            List<vmSpec> toRet = new List<vmSpec>();
            foreach (int vmID in VMIDs)
                toRet.Add(getVMByDBID_nolocking(vmID));

            return toRet;
        }

        public string[] getBladesByAllocatedServer(string NodeIP)
        {
            string sqlCommand = "select bladeIP from bladeOwnership " +
                                "join bladeConfiguration on ownershipKey = bladeConfiguration.ownershipID " +
                                "where bladeOwnership.currentOwner = $bladeOwner";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                cmd.Parameters.AddWithValue("$bladeOwner", NodeIP);
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    List<string> toRet = new List<string>(10);
                    while (reader.Read())
                        toRet.Add((string)reader["bladeIP"]);
                    return toRet.ToArray();
                }
            }
        }

        public disposingListOfBladesAndVMs getBladesAndVMs(Func<bladeSpec, bool> BladeFilter, Func<vmSpec, bool> VMFilter, bladeLockType lockTypeRead, bladeLockType lockTypeWrite, bool permitAccessDuringBIOS = false, bool permitAccessDuringDeployment = false)
        {
            disposingListOfBladesAndVMs toRet = new disposingListOfBladesAndVMs();
            toRet.blades = getAllBladeInfo(BladeFilter, lockTypeRead, lockTypeWrite, permitAccessDuringBIOS, permitAccessDuringDeployment);
            toRet.VMs = getAllVMInfo(VMFilter, lockTypeRead, lockTypeWrite);

            return toRet;
        }

        public GetBladeStatusResult getBladeStatus(string nodeIp, string requestorIp)
        {
            using (lockableBladeSpec blade = getBladeByIP(nodeIp, bladeLockType.lockOwnership, bladeLockType.lockNone,
                permitAccessDuringBIOS: true, permitAccessDuringDeployment: true))
            {
                switch (blade.spec.state)
                {
                    case bladeStatus.unused:
                        return GetBladeStatusResult.unused;
                    case bladeStatus.releaseRequested:
                        return GetBladeStatusResult.releasePending;
                    case bladeStatus.inUse:
                        if (blade.spec.currentOwner == requestorIp)
                            return GetBladeStatusResult.yours;
                        return GetBladeStatusResult.notYours;
                    case bladeStatus.inUseByDirector:
                        return GetBladeStatusResult.notYours;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        // TODO: reduce duplication with above
        public GetBladeStatusResult getVMStatus(string nodeIp, string requestorIp)
        {
            using (lockableVMSpec blade = getVMByIP(nodeIp, bladeLockType.lockOwnership, bladeLockType.lockNone))
            {
                switch (blade.spec.state)
                {
                    case bladeStatus.unused:
                        return GetBladeStatusResult.unused;
                    case bladeStatus.releaseRequested:
                        return GetBladeStatusResult.releasePending;
                    case bladeStatus.inUse:
                        if (blade.spec.currentOwner == requestorIp)
                            return GetBladeStatusResult.yours;
                        return GetBladeStatusResult.notYours;
                    case bladeStatus.inUseByDirector:
                        return GetBladeStatusResult.notYours;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        public void initWithBlades(bladeSpec[] bladeSpecs)
        {
            dropDB();
            createTables();

            // Since we disposed and recreated the DBConnection, we'll need to update each bladeSpec with the new one, and
            // blow away any DB IDs.
            foreach (bladeSpec spec in bladeSpecs)
            {
                spec.conn = conn;
                spec.ownershipRowID = null;
                spec.bladeID = null;
            }

            foreach (bladeSpec spec in bladeSpecs)
                addNode(spec);
        }            

        public void addNode(bladeOwnership spec)
        {
            spec.createOrUpdateInDB();
        }

        public void makeIntoAVMServer(lockableBladeSpec toConvert)
        {
            // Delete any VM configurations that have been left lying around.
            string sql = "select bladeConfigKey from VMConfiguration " +
                            " join BladeConfiguration on  BladeConfigKey = ownershipKey " +
                            "join bladeOwnership on VMConfiguration.parentBladeID = ownershipKey " +
                            " where bladeConfigKey = $bladeIP";
            List<long> toDel = new List<long>();
            using (SQLiteCommand cmd = new SQLiteCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("$bladeIP", toConvert.spec.bladeIP);
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        toDel.Add((long) reader[0]);
                    }
                }
            }

            string deleteSQL = "delete from VMConfiguration where id in (" + String.Join(",", toDel) + ")";
            using (SQLiteCommand cmd = new SQLiteCommand(deleteSQL, conn))
            {
                cmd.ExecuteNonQuery();
            }

            // And then mark this blade as being a VM server.
            toConvert.spec.currentlyBeingAVMServer = true;
            toConvert.spec.state = bladeStatus.inUseByDirector;
            // Since we don't know if the blade has been left in a good state (or even if it was a VM server previously) we 
            // force a power cycle before we use it.
            toConvert.spec.vmDeployState = VMDeployStatus.needsPowerCycle;
        }

        public void refreshKeepAliveForRequestor(string requestorIP)
        {
            string sqlCommand = "update bladeOwnership set lastKeepAlive=$NOW " +
                                "where currentOwner = $ownerIP";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                cmd.Parameters.AddWithValue("$ownerIP", requestorIP);
                cmd.Parameters.AddWithValue("$NOW", DateTime.Now.ToString());
                cmd.ExecuteNonQuery();
            }
        }

        public vmserverTotals getVMServerTotalsByVMServerIP(string vmServerIP)
        {
            // You should hold a lock on the VM server before calling this, to ensure the result doesn't change before you get
            // a chance to use it.

            string sqlCommand = "select sum(cpucount) as cpus, sum(memoryMB) as ram, count(*) as VMs " +
                                " from vmConfiguration " +
                                "join bladeConfiguration on parentbladeID = bladeConfigKey " +
                                "where bladeIP = $vmServerIP";
            using (SQLiteCommand cmd = new SQLiteCommand(sqlCommand, conn))
            {
                cmd.Parameters.AddWithValue("$vmServerIP", vmServerIP);
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        throw new Exception();
                    return new vmserverTotals(reader);
                }                    
            }
        }

        public void Dispose()
        {
            conn.Dispose();
        }
    }

    public class currentOwnerStat
    {
        public string ownerName;
        public float allocatedBlades;

        public currentOwnerStat(string newOwnerName, int newAllocatedBlades )
        {
            ownerName = newOwnerName;
            allocatedBlades = newAllocatedBlades;
        }
    }

    public class vmserverTotals
    {
        public readonly int cpus;
        public readonly int ram;
        public readonly int VMs;

        public vmserverTotals(int newCpus, int newRAM, int newVMs)
        {
            cpus = newCpus;
            ram = newRAM;
            VMs = newVMs;
        }

        public vmserverTotals(SQLiteDataReader row)
        {
            cpus = ram = VMs = 0;
            if (!(row["cpus"] is DBNull))
                cpus = Convert.ToInt32((long)row["cpus"]);
            if (!(row["ram"] is DBNull))
                ram = Convert.ToInt32((long)row["ram"]);
            if (!(row["VMs"] is DBNull))
                VMs = Convert.ToInt32( (long)row["VMs"]);
        }
    }
}