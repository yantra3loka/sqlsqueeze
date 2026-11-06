// SqlSqueeze Linux VDI shim.
//
// The Linux VDI API (libsqlvdi.so) exposes C++ classes, which .NET
// P/Invoke cannot call directly (name mangling, thiscall). This tiny
// wrapper exposes the pieces SqlSqueeze needs as plain C functions.
//
// Build on the SQL Server machine (see Makefile); it links against
// /opt/mssql/lib/libsqlvdi.so, which ships with SQL Server on Linux.
//
// Part of SqlSqueeze (GPL v3 or later, like the rest of this repo).

#include "vdi.h"
#include <cstddef>
#include <new>

extern "C" {

typedef void* MsbpHandle;

MsbpHandle msbp_vdi_set_new(void)
{
    return static_cast<MsbpHandle>(new (std::nothrow) ClientVirtualDeviceSet());
}

void msbp_vdi_set_free(MsbpHandle handle)
{
    delete static_cast<ClientVirtualDeviceSet*>(handle);
}

int msbp_vdi_set_create(MsbpHandle handle, const char* name, VDConfig* cfg)
{
    // SQL Server on Linux has no named instances: only the set name.
    return static_cast<ClientVirtualDeviceSet*>(handle)->Create(
        const_cast<char*>(name), cfg);
}

int msbp_vdi_set_get_configuration(MsbpHandle handle, long long timeoutSecs, VDConfig* cfg)
{
    return static_cast<ClientVirtualDeviceSet*>(handle)->GetConfiguration(
        static_cast<time_t>(timeoutSecs), cfg);
}

int msbp_vdi_set_open_device(MsbpHandle handle, const char* name, MsbpHandle* deviceOut)
{
    ClientVirtualDevice* device = 0;
    int rc = static_cast<ClientVirtualDeviceSet*>(handle)->OpenDevice(
        const_cast<char*>(name), &device);
    *deviceOut = static_cast<MsbpHandle>(device);
    return rc;
}

int msbp_vdi_set_close(MsbpHandle handle)
{
    return static_cast<ClientVirtualDeviceSet*>(handle)->Close();
}

int msbp_vdi_set_signal_abort(MsbpHandle handle)
{
    return static_cast<ClientVirtualDeviceSet*>(handle)->SignalAbort();
}

int msbp_vdi_dev_get_command(MsbpHandle device, long long timeoutSecs, VDC_Command** cmdOut)
{
    return static_cast<ClientVirtualDevice*>(device)->GetCommand(
        static_cast<time_t>(timeoutSecs), cmdOut);
}

int msbp_vdi_dev_complete_command(MsbpHandle device, VDC_Command* cmd,
    int completionCode, unsigned long bytesTransferred, long long position)
{
    return static_cast<ClientVirtualDevice*>(device)->CompleteCommand(
        cmd, completionCode, bytesTransferred, static_cast<int64_t>(position));
}

// The ClientVirtualDevice pointer returned by OpenDevice is owned by the
// device set (Microsoft's own sample never deletes it); the set's Close
// releases everything. Deleting it here corrupts the heap
// ("free(): invalid pointer" at the end of a working backup).
void msbp_vdi_dev_free(MsbpHandle device)
{
    (void)device;
}

} // extern "C"
