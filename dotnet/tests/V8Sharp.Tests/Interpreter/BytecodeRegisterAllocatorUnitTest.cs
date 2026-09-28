// Port of test/unittests/interpreter/bytecode-register-allocator-unittest.cc.
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public class BytecodeRegisterAllocatorUnitTest
{
    readonly BytecodeRegisterAllocator _allocator = new(0);

    BytecodeRegisterAllocator allocator() => _allocator;

    [Fact]
    public void BytecodeRegisterAllocatorTest_SimpleAllocations()
    {
        Assert.Equal(0, allocator().MaximumRegisterCount());
        Register reg0 = allocator().NewRegister();
        Assert.Equal(0, reg0.Index);
        Assert.Equal(1, allocator().MaximumRegisterCount());
        Assert.Equal(1, allocator().NextRegisterIndex());
        Assert.True(allocator().RegisterIsLive(reg0));

        allocator().ReleaseRegisters(0);
        Assert.False(allocator().RegisterIsLive(reg0));
        Assert.Equal(1, allocator().MaximumRegisterCount());
        Assert.Equal(0, allocator().NextRegisterIndex());

        reg0 = allocator().NewRegister();
        Register reg1 = allocator().NewRegister();
        Assert.Equal(0, reg0.Index);
        Assert.Equal(1, reg1.Index);
        Assert.True(allocator().RegisterIsLive(reg0));
        Assert.True(allocator().RegisterIsLive(reg1));
        Assert.Equal(2, allocator().MaximumRegisterCount());
        Assert.Equal(2, allocator().NextRegisterIndex());

        allocator().ReleaseRegisters(1);
        Assert.True(allocator().RegisterIsLive(reg0));
        Assert.False(allocator().RegisterIsLive(reg1));
        Assert.Equal(2, allocator().MaximumRegisterCount());
        Assert.Equal(1, allocator().NextRegisterIndex());
    }

    [Fact]
    public void BytecodeRegisterAllocatorTest_RegisterListAllocations()
    {
        Assert.Equal(0, allocator().MaximumRegisterCount());
        RegisterList reg_list = allocator().NewRegisterList(3);
        Assert.Equal(0, reg_list.FirstRegister().Index);
        Assert.Equal(3, reg_list.RegisterCount);
        Assert.Equal(0, reg_list[0].Index);
        Assert.Equal(1, reg_list[1].Index);
        Assert.Equal(2, reg_list[2].Index);
        Assert.Equal(3, allocator().MaximumRegisterCount());
        Assert.Equal(3, allocator().NextRegisterIndex());
        Assert.True(allocator().RegisterIsLive(reg_list[2]));

        Register reg = allocator().NewRegister();
        RegisterList reg_list_2 = allocator().NewRegisterList(2);
        Assert.Equal(3, reg.Index);
        Assert.Equal(4, reg_list_2.FirstRegister().Index);
        Assert.Equal(2, reg_list_2.RegisterCount);
        Assert.Equal(4, reg_list_2[0].Index);
        Assert.Equal(5, reg_list_2[1].Index);
        Assert.Equal(6, allocator().MaximumRegisterCount());
        Assert.Equal(6, allocator().NextRegisterIndex());
        Assert.True(allocator().RegisterIsLive(reg));
        Assert.True(allocator().RegisterIsLive(reg_list_2[1]));

        allocator().ReleaseRegisters(reg.Index);
        Assert.False(allocator().RegisterIsLive(reg));
        Assert.False(allocator().RegisterIsLive(reg_list_2[0]));
        Assert.False(allocator().RegisterIsLive(reg_list_2[1]));
        Assert.True(allocator().RegisterIsLive(reg_list[2]));
        Assert.Equal(6, allocator().MaximumRegisterCount());
        Assert.Equal(3, allocator().NextRegisterIndex());

        RegisterList empty_reg_list = allocator().NewRegisterList(0);
        Assert.Equal(0, empty_reg_list.FirstRegister().Index);
        Assert.Equal(0, empty_reg_list.RegisterCount);
        Assert.Equal(6, allocator().MaximumRegisterCount());
        Assert.Equal(3, allocator().NextRegisterIndex());
    }

    [Fact]
    public void BytecodeRegisterAllocatorTest_GrowableRegisterListAllocations()
    {
        Assert.Equal(0, allocator().MaximumRegisterCount());
        Register reg = allocator().NewRegister();
        Assert.Equal(0, reg.Index);
        RegisterList reg_list = allocator().NewGrowableRegisterList();
        Assert.Equal(0, reg_list.RegisterCount);
        allocator().GrowRegisterList(ref reg_list);
        allocator().GrowRegisterList(ref reg_list);
        allocator().GrowRegisterList(ref reg_list);
        Assert.Equal(3, reg_list.RegisterCount);
        Assert.Equal(1, reg_list[0].Index);
        Assert.Equal(2, reg_list[1].Index);
        Assert.Equal(3, reg_list[2].Index);
        Assert.Equal(4, allocator().MaximumRegisterCount());
        Assert.Equal(4, allocator().NextRegisterIndex());
    }
}
