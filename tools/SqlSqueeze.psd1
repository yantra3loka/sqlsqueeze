@{
    ModuleVersion = '1.6.2'
    GUID = 'cad80d59-dc89-4db6-b81a-3907f1a1e687'
    Author = 'Krishnan Venkiteswaran'
    Description = 'PowerShell wrapper for SqlSqueeze (open-source SQL Server compressed backup).'
    PowerShellVersion = '7.0'
    RootModule = 'SqlSqueeze.psm1'
    FunctionsToExport = @('Backup-SqlDatabase', 'Restore-SqlDatabase', 'Remove-SqlBackup', 'Test-SqlBackup', 'Test-SqlSqueezeBackup', 'Restore-SqlChain')
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            ProjectUri = 'https://github.com/yantra3loka/sqlsqueeze'
            LicenseUri = 'https://www.gnu.org/licenses/gpl-3.0.html'
        }
    }
}
