// Port of test/unittests/interpreter/bytecode-register-optimizer-unittest.cc.
using V8Sharp.Interpreter;

namespace V8Sharp.Tests.Interpreter;

public class BytecodeRegisterOptimizerUnitTest : BytecodeRegisterOptimizer.IBytecodeWriter
{
    readonly record struct RegisterTransfer(Bytecode Bytecode, Register Input, Register Output);

    BytecodeRegisterAllocator _registerAllocator = null!;
    BytecodeRegisterOptimizer _registerOptimizer = null!;
    readonly List<RegisterTransfer> _output = [];

    void Initialize(int number_of_parameters, int number_of_locals)
    {
        _registerAllocator = new BytecodeRegisterAllocator(number_of_locals);
        _registerOptimizer = new BytecodeRegisterOptimizer(_registerAllocator, number_of_locals, number_of_parameters,
                                                           this);
    }

    void BytecodeRegisterOptimizer.IBytecodeWriter.EmitLdar(Register input) => _output.Add(new(Bytecode.Ldar, input, Register.InvalidValue()));
    void BytecodeRegisterOptimizer.IBytecodeWriter.EmitStar(Register output) => _output.Add(new(Bytecode.Star, Register.InvalidValue(), output));
    void BytecodeRegisterOptimizer.IBytecodeWriter.EmitMov(Register input, Register output) => _output.Add(new(Bytecode.Mov, input, output));

    BytecodeRegisterAllocator allocator() => _registerAllocator;
    BytecodeRegisterOptimizer optimizer() => _registerOptimizer;

    Register NewTemporary() => allocator().NewRegister();

    void ReleaseTemporaries(Register reg) => allocator().ReleaseRegisters(reg.Index);

    int write_count() => _output.Count;
    List<RegisterTransfer> output() => _output;

    [Fact]
    public void BytecodeRegisterOptimizerTest_TemporaryMaterializedForFlush()
    {
        Initialize(1, 1);
        Register temp = NewTemporary();
        optimizer().DoStar(temp);
        Assert.Equal(0, write_count());
        optimizer().Flush();
        Assert.Equal(1, write_count());
        Assert.Equal(Bytecode.Star, output()[0].Bytecode);
        Assert.Equal(temp.Index, output()[0].Output.Index);
    }

    [Fact]
    public void BytecodeRegisterOptimizerTest_TemporaryMaterializedForJump()
    {
        Initialize(1, 1);
        Register temp = NewTemporary();
        optimizer().DoStar(temp);
        Assert.Equal(0, write_count());
        optimizer().PrepareForBytecode(Bytecode.Jump, ImplicitRegisterUse.None);
        Assert.Equal(1, write_count());
        Assert.Equal(Bytecode.Star, output()[0].Bytecode);
        Assert.Equal(temp.Index, output()[0].Output.Index);
    }

    [Fact]
    public void BytecodeRegisterOptimizerTest_TemporaryNotEmitted()
    {
        Initialize(3, 1);
        Register parameter = Register.FromParameterIndex(1);
        optimizer().DoLdar(parameter);
        Assert.Equal(0, write_count());
        Register temp = NewTemporary();
        optimizer().DoStar(temp);
        ReleaseTemporaries(temp);
        Assert.Equal(0, write_count());
        optimizer().PrepareForBytecode(Bytecode.Return, ImplicitRegisterUse.ReadAccumulator);
        Assert.Equal(Bytecode.Ldar, output()[0].Bytecode);
        Assert.Equal(parameter.Index, output()[0].Input.Index);
    }

    [Fact]
    public void BytecodeRegisterOptimizerTest_ReleasedRegisterUsed()
    {
        Initialize(3, 1);
        optimizer().PrepareForBytecode(Bytecode.LdaSmi, ImplicitRegisterUse.WriteAccumulator);
        Register temp0 = NewTemporary();
        Register temp1 = NewTemporary();
        optimizer().DoStar(temp1);
        Assert.Equal(0, write_count());
        optimizer().PrepareForBytecode(Bytecode.LdaSmi, ImplicitRegisterUse.WriteAccumulator);
        Assert.Equal(1, write_count());
        Assert.Equal(Bytecode.Star, output()[0].Bytecode);
        Assert.Equal(temp1.Index, output()[0].Output.Index);
        optimizer().DoMov(temp1, temp0);
        Assert.Equal(1, write_count());
        ReleaseTemporaries(temp1);
        Assert.Equal(1, write_count());
        optimizer().DoLdar(temp0);
        Assert.Equal(1, write_count());
        optimizer().PrepareForBytecode(Bytecode.Return, ImplicitRegisterUse.ReadAccumulator);
        Assert.Equal(2, write_count());
        Assert.Equal(Bytecode.Ldar, output()[1].Bytecode);
        Assert.Equal(temp1.Index, output()[1].Input.Index);
    }

    [Fact]
    public void BytecodeRegisterOptimizerTest_ReleasedRegisterNotFlushed()
    {
        Initialize(3, 1);
        optimizer().PrepareForBytecode(Bytecode.LdaSmi, ImplicitRegisterUse.WriteAccumulator);
        Register temp0 = NewTemporary();
        Register temp1 = NewTemporary();
        optimizer().DoStar(temp0);
        Assert.Equal(0, write_count());
        optimizer().DoStar(temp1);
        Assert.Equal(0, write_count());
        ReleaseTemporaries(temp1);
        optimizer().Flush();
        Assert.Equal(1, write_count());
        Assert.Equal(Bytecode.Star, output()[0].Bytecode);
        Assert.Equal(temp0.Index, output()[0].Output.Index);
    }

    [Fact]
    public void BytecodeRegisterOptimizerTest_StoresToLocalsImmediate()
    {
        Initialize(3, 1);
        Register parameter = Register.FromParameterIndex(1);
        optimizer().DoLdar(parameter);
        Assert.Equal(0, write_count());
        var local = new Register(0);
        optimizer().DoStar(local);
        Assert.Equal(1, write_count());
        Assert.Equal(Bytecode.Mov, output()[0].Bytecode);
        Assert.Equal(parameter.Index, output()[0].Input.Index);
        Assert.Equal(local.Index, output()[0].Output.Index);

        optimizer().PrepareForBytecode(Bytecode.Return, ImplicitRegisterUse.ReadAccumulator);
        Assert.Equal(2, write_count());
        Assert.Equal(Bytecode.Ldar, output()[1].Bytecode);
        Assert.Equal(local.Index, output()[1].Input.Index);
    }

    [Fact]
    public void BytecodeRegisterOptimizerTest_SingleTemporaryNotMaterializedForInput()
    {
        Initialize(3, 1);
        Register parameter = Register.FromParameterIndex(1);
        Register temp0 = NewTemporary();
        Register temp1 = NewTemporary();
        optimizer().DoMov(parameter, temp0);
        optimizer().DoMov(parameter, temp1);
        Assert.Equal(0, write_count());

        Register reg = optimizer().GetInputRegister(temp0);
        RegisterList reg_list = optimizer().GetInputRegisterList(BytecodeUtils.NewRegisterList(temp0.Index, 1));
        Assert.Equal(0, write_count());
        Assert.Equal(parameter.Index, reg.Index);
        Assert.Equal(parameter.Index, reg_list.FirstRegister().Index);
        Assert.Equal(1, reg_list.RegisterCount);
    }

    [Fact]
    public void BytecodeRegisterOptimizerTest_RangeOfTemporariesMaterializedForInput()
    {
        Initialize(3, 1);
        Register parameter = Register.FromParameterIndex(1);
        Register temp0 = NewTemporary();
        Register temp1 = NewTemporary();
        optimizer().PrepareForBytecode(Bytecode.LdaSmi, ImplicitRegisterUse.WriteAccumulator);
        optimizer().DoStar(temp0);
        optimizer().DoMov(parameter, temp1);
        Assert.Equal(0, write_count());

        optimizer().PrepareForBytecode(Bytecode.CallJSRuntime, ImplicitRegisterUse.WriteAccumulator);
        RegisterList reg_list = optimizer().GetInputRegisterList(BytecodeUtils.NewRegisterList(temp0.Index, 2));
        Assert.Equal(temp0.Index, reg_list.FirstRegister().Index);
        Assert.Equal(2, reg_list.RegisterCount);
        Assert.Equal(2, write_count());
        Assert.Equal(Bytecode.Star, output()[0].Bytecode);
        Assert.Equal(temp0.Index, output()[0].Output.Index);
        Assert.Equal(Bytecode.Mov, output()[1].Bytecode);
        Assert.Equal(parameter.Index, output()[1].Input.Index);
        Assert.Equal(temp1.Index, output()[1].Output.Index);
    }
}
