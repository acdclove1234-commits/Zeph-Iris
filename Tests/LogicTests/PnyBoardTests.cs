// PNY のグラボを PCI の番号で見分ける（名前・LED の並びが同じでも、確かめた基板だけを直接書く）
using ProFanController;

namespace LogicTests;

public static class PnyBoardTests
{
    public static void TestVerifiedBoardById()
    {
        T.Check(PnyEpicXGpu.IsVerifiedBoard(0x2C0510DE, 0x143A196E), "the verified 5070 Ti Epic-X board (device 2C05, board 143A, PNY)");
        T.Check(!PnyEpicXGpu.IsVerifiedBoard(0x2C0510DE, 0x1440196E), "same GPU, another PNY board is not verified");
        T.Check(!PnyEpicXGpu.IsVerifiedBoard(0x2C0410DE, 0x143A196E), "another GPU chip with the same board number is not");
        T.Check(!PnyEpicXGpu.IsVerifiedBoard(0x2C0510DE, 0x143A1043), "another vendor (not PNY) is not");
        T.Check(!PnyEpicXGpu.IsVerifiedBoard(0x2C051002, 0x143A196E), "a non-NVIDIA vendor id is not");
    }
}
