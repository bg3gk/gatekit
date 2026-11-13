using GateKit.Symbols.Demangling;

namespace GateKit.Symbols.Tests;

/// <summary>
/// Expected strings come from dbghelp's UnDecorateSymbolName on Windows (flags 0, 0x2 and 0x1000) for
/// synthetic names; every row checks all three flag sets.
/// </summary>
public class MsvcDemanglerTests
{
    private static void Check(string mangled, string complete, string noMsKeywords, string nameOnly)
    {
        Assert.Equal(complete, MsvcDemangler.Undecorate(mangled, UndecorateFlags.Complete));
        Assert.Equal(noMsKeywords, MsvcDemangler.Undecorate(mangled, UndecorateFlags.NoMsKeywords));
        Assert.Equal(nameOnly, MsvcDemangler.Undecorate(mangled, UndecorateFlags.NameOnly));
    }

    /// <summary>Free functions and methods: access, static, virtual, this qualifiers, calling conventions, primitive types.</summary>
    [Theory]
    [InlineData("?Foo@Bar@ns@@QEAAXH@Z",
        "public: void __cdecl ns::Bar::Foo(int) __ptr64",
        "public: void ns::Bar::Foo(int)",
        "ns::Bar::Foo")]
    [InlineData("?Get@Widget@ui@@QEBAPEBVNode@2@H@Z",
        "public: class ui::Node const * __ptr64 __cdecl ui::Widget::Get(int)const __ptr64",
        "public: class ui::Node const * ui::Widget::Get(int)const ",
        "ui::Widget::Get")]
    [InlineData("?Update@Widget@ui@@MEAAXM@Z",
        "protected: virtual void __cdecl ui::Widget::Update(float) __ptr64",
        "protected: virtual void ui::Widget::Update(float)",
        "ui::Widget::Update")]
    [InlineData("?Hidden@Widget@ui@@AEAA_NXZ",
        "private: bool __cdecl ui::Widget::Hidden(void) __ptr64",
        "private: bool ui::Widget::Hidden(void)",
        "ui::Widget::Hidden")]
    [InlineData("?f@Foo@@SAXXZ",
        "public: static void __cdecl Foo::f(void)",
        "public: static void Foo::f(void)",
        "Foo::f")]
    [InlineData("?f@Foo@@CAXXZ",
        "private: static void __cdecl Foo::f(void)",
        "private: static void Foo::f(void)",
        "Foo::f")]
    [InlineData("?f@Foo@@UEBAXXZ",
        "public: virtual void __cdecl Foo::f(void)const __ptr64",
        "public: virtual void Foo::f(void)const ",
        "Foo::f")]
    [InlineData("?f@Foo@@QEDAXXZ",
        "public: void __cdecl Foo::f(void)const volatile __ptr64",
        "public: void Foo::f(void)const volatile ",
        "Foo::f")]
    [InlineData("?f@Foo@@QEIAAXXZ",
        "public: void __cdecl Foo::f(void) __ptr64 __restrict",
        "public: void Foo::f(void)",
        "Foo::f")]
    [InlineData("?Name@Item@@QEGBAPEBDXZ",
        "public: char const * __ptr64 __cdecl Item::Name(void)const __ptr64& ",
        "public: char const * Item::Name(void)const & ",
        "Item::Name")]
    [InlineData("?Take@Item@@QEHAA?AV1@XZ",
        "public: class Item __cdecl Item::Take(void) __ptr64&& ",
        "public: class Item Item::Take(void)&& ",
        "Item::Take")]
    [InlineData("?f@Foo@@QAEXXZ",
        "public: void __thiscall Foo::f(void)",
        "public: void Foo::f(void)",
        "Foo::f")]
    [InlineData("?f@ns@@YAXXZ",
        "void __cdecl ns::f(void)",
        "void ns::f(void)",
        "ns::f")]
    [InlineData("?f@@YGXXZ",
        "void __stdcall f(void)",
        "void f(void)",
        "f")]
    [InlineData("?f@@YQXXZ",
        "void __vectorcall f(void)",
        "void f(void)",
        "f")]
    [InlineData("?f@@YAXZZ",
        "void __cdecl f(...)",
        "void f(...)",
        "f")]
    [InlineData("?f@@YAXHZZ",
        "void __cdecl f(int,...)",
        "void f(int,...)",
        "f")]
    [InlineData("?f@@YAXH@_E",
        "void __cdecl f(int) noexcept",
        "void f(int) noexcept",
        "f")]
    [InlineData("?f@@YAX_W_S_U_Q_J_K_L_M_N@Z",
        "void __cdecl f(wchar_t,char16_t,char32_t,char8_t,__int64,unsigned __int64,__int128,unsigned __int128,bool)",
        "void f(wchar_t,char16_t,char32_t,char8_t,__int64,unsigned __int64,__int128,unsigned __int128,bool)",
        "f")]
    [InlineData("?f@@YAXCDEFGIJKMNO@Z",
        "void __cdecl f(signed char,char,unsigned char,short,unsigned short,unsigned int,long,unsigned long,float,double,long double)",
        "void f(signed char,char,unsigned char,short,unsigned short,unsigned int,long,unsigned long,float,double,long double)",
        "f")]
    [InlineData("?f@@YAX_D_E_F_G_H_I@Z",
        "void __cdecl f(__int8,unsigned __int8,__int16,unsigned __int16,__int32,unsigned __int32)",
        "void f(__int8,unsigned __int8,__int16,unsigned __int16,__int32,unsigned __int32)",
        "f")]
    [InlineData("?f@@YAXW4E@@@Z",
        "void __cdecl f(enum E)",
        "void f(enum E)",
        "f")]
    [InlineData("?f@@YAXW0E@@@Z",
        "void __cdecl f(enum char E)",
        "void f(enum char E)",
        "f")]
    [InlineData("?f@@YAXTU@@@Z",
        "void __cdecl f(union U)",
        "void f(union U)",
        "f")]
    [InlineData("?f@@YAX$$TAEA$$T@Z",
        "void __cdecl f(std::nullptr_t,std::nullptr_t & __ptr64)",
        "void f(std::nullptr_t,std::nullptr_t &)",
        "f")]
    [InlineData("?f@@YAX_$H@Z",
        "void __cdecl f(__w64 int)",
        "void f(__w64 int)",
        "f")]
    [InlineData("?f@@YMPE$AAVString@System@@XZ",
        "class System::String ^ __ptr64 __clrcall f(void)",
        "class System::String ^ f(void)",
        "f")]
    public void Functions(string mangled, string complete, string noMsKeywords, string nameOnly) =>
        Check(mangled, complete, noMsKeywords, nameOnly);

    /// <summary>Constructors, destructors and operators, including conversion operators and operator templates.</summary>
    [Theory]
    [InlineData("??0Widget@ui@@QEAA@AEBV01@@Z",
        "public: __cdecl ui::Widget::Widget(class ui::Widget const & __ptr64) __ptr64",
        "public: ui::Widget::Widget(class ui::Widget const &)",
        "ui::Widget::Widget")]
    [InlineData("??1Widget@ui@@UEAA@XZ",
        "public: virtual __cdecl ui::Widget::~Widget(void) __ptr64",
        "public: virtual ui::Widget::~Widget(void)",
        "ui::Widget::~Widget")]
    [InlineData("??0?$A@H@@QEAA@XZ",
        "public: __cdecl A<int>::A<int>(void) __ptr64",
        "public: A<int>::A<int>(void)",
        "A<int>::A<int>")]
    [InlineData("??1?$A@H@@QEAA@XZ",
        "public: __cdecl A<int>::~A<int>(void) __ptr64",
        "public: A<int>::~A<int>(void)",
        "A<int>::~A<int>")]
    [InlineData("??4Foo@@QEAAAEAV0@AEBV0@@Z",
        "public: class Foo & __ptr64 __cdecl Foo::operator=(class Foo const & __ptr64) __ptr64",
        "public: class Foo & Foo::operator=(class Foo const &)",
        "Foo::operator=")]
    [InlineData("??M?$Foo@H@@QEBA_NAEBV0@@Z",
        "public: bool __cdecl Foo<int>::operator<(class Foo<int> const & __ptr64)const __ptr64",
        "public: bool Foo<int>::operator<(class Foo<int> const &)const ",
        "Foo<int>::operator<")]
    [InlineData("??RFoo@@QEAAHH@Z",
        "public: int __cdecl Foo::operator()(int) __ptr64",
        "public: int Foo::operator()(int)",
        "Foo::operator()")]
    [InlineData("??AFoo@@QEAAAEAHH@Z",
        "public: int & __ptr64 __cdecl Foo::operator[](int) __ptr64",
        "public: int & Foo::operator[](int)",
        "Foo::operator[]")]
    [InlineData("??CFoo@@QEAAPEAV0@XZ",
        "public: class Foo * __ptr64 __cdecl Foo::operator->(void) __ptr64",
        "public: class Foo * Foo::operator->(void)",
        "Foo::operator->")]
    [InlineData("??JFoo@@QEAAHH@Z",
        "public: int __cdecl Foo::operator->*(int) __ptr64",
        "public: int Foo::operator->*(int)",
        "Foo::operator->*")]
    [InlineData("??QFoo@@QEAAHH@Z",
        "public: int __cdecl Foo::operator,(int) __ptr64",
        "public: int Foo::operator,(int)",
        "Foo::operator,")]
    [InlineData("??_4Foo@@QEAAAEAV0@AEBV0@@Z",
        "public: class Foo & __ptr64 __cdecl Foo::operator&=(class Foo const & __ptr64) __ptr64",
        "public: class Foo & Foo::operator&=(class Foo const &)",
        "Foo::operator&=")]
    [InlineData("??__MFoo@@QEBA?AUstrong_ordering@std@@AEBV0@@Z",
        "public: struct std::strong_ordering __cdecl Foo::operator<=>(class Foo const & __ptr64)const __ptr64",
        "public: struct std::strong_ordering Foo::operator<=>(class Foo const &)const ",
        "Foo::operator<=>")]
    [InlineData("??BFoo@@QEBA_NXZ",
        "public: __cdecl Foo::operator bool(void)const __ptr64",
        "public: Foo::operator bool(void)const ",
        "Foo::operator bool")]
    [InlineData("??B?$A@H@@QEAA?AV?$B@H@@XZ",
        "public: __cdecl A<int>::operator class B<int>(void) __ptr64",
        "public: A<int>::operator class B<int>(void)",
        "A<int>::operator B<int>")]
    [InlineData("??$?BH@Foo@@QEAAHXZ",
        "public: __cdecl Foo::operator<int> int(void) __ptr64",
        "public: Foo::operator<int> int(void)",
        "Foo::operator<int> int")]
    [InlineData("??$?6H@@YAXH@Z",
        "void __cdecl operator<<<int>(int)",
        "void operator<<<int>(int)",
        "operator<<<int>")]
    [InlineData("??$?MH@@YA_NHH@Z",
        "bool __cdecl operator<<int>(int,int)",
        "bool operator<<int>(int,int)",
        "operator<<int>")]
    [InlineData("??2@YAPEAX_K@Z",
        "void * __ptr64 __cdecl operator new(unsigned __int64)",
        "void * operator new(unsigned __int64)",
        "operator new")]
    [InlineData("??3@YAXPEAX@Z",
        "void __cdecl operator delete(void * __ptr64)",
        "void operator delete(void *)",
        "operator delete")]
    [InlineData("??_U@YAPEAX_K@Z",
        "void * __ptr64 __cdecl operator new[](unsigned __int64)",
        "void * operator new[](unsigned __int64)",
        "operator new[]")]
    [InlineData("??_VFoo@@SAXPEAX@Z",
        "public: static void __cdecl Foo::operator delete[](void * __ptr64)",
        "public: static void Foo::operator delete[](void *)",
        "Foo::operator delete[]")]
    [InlineData("??R<lambda_9f3a>@@QEBA@H@Z",
        "public: __cdecl <lambda_9f3a>::operator()(int)const __ptr64",
        "public: <lambda_9f3a>::operator()(int)const ",
        "<lambda_9f3a>::operator()")]
    public void SpecialMembers(string mangled, string complete, string noMsKeywords, string nameOnly) =>
        Check(mangled, complete, noMsKeywords, nameOnly);

    /// <summary>Template instances: nesting, literal and pointer arguments, packs, lambdas, anonymous namespaces.</summary>
    [Theory]
    [InlineData("??$Max@H@math@@YAHHH@Z",
        "int __cdecl math::Max<int>(int,int)",
        "int math::Max<int>(int,int)",
        "math::Max<int>")]
    [InlineData("?Set@?$Array@H$0BA@@@QEAAX_KH@Z",
        "public: void __cdecl Array<int,16>::Set(unsigned __int64,int) __ptr64",
        "public: void Array<int,16>::Set(unsigned __int64,int)",
        "Array<int,16>::Set")]
    [InlineData("?Tag@?$Holder@$0?5@@QEAAXXZ",
        "public: void __cdecl Holder<-6>::Tag(void) __ptr64",
        "public: void Holder<-6>::Tag(void)",
        "Holder<-6>::Tag")]
    [InlineData("?Get@?$Holder@$1?g_value@@3HA@@SAPEAHXZ",
        "public: static int * __ptr64 __cdecl Holder<&int g_value>::Get(void)",
        "public: static int * Holder<&int g_value>::Get(void)",
        "Holder<&g_value>::Get")]
    [InlineData("?f@@YAXV?$A@$1?f@Foo@@QEAAXXZ@@@Z",
        "void __cdecl f(class A<&public: void __cdecl Foo::f(void) __ptr64>)",
        "void f(class A<&public: void Foo::f(void)>)",
        "f")]
    [InlineData("?f@@YAXV?$A@$E?x@@3HA@@@Z",
        "void __cdecl f(class A<int x>)",
        "void f(class A<int x>)",
        "f")]
    [InlineData("?f@@YAXV?$A@$0A@$00$0BA@$0?1$0?BA@$0PPPPPPPP@$0IAAAAAAAAAAAAAAA@@@@Z",
        "void __cdecl f(class A<0,1,16,-2,-16,4294967295,9223372036854775808>)",
        "void f(class A<0,1,16,-2,-16,4294967295,9223372036854775808>)",
        "f")]
    [InlineData("?f@@YAXV?$A@$MH0A@@@@Z",
        "void __cdecl f(class A<0>)",
        "void f(class A<0>)",
        "f")]
    [InlineData("?f@@YAXV?$A@$1@@@@Z",
        "void __cdecl f(class A<NULL>)",
        "void f(class A<NULL>)",
        "f")]
    [InlineData("?f@@YAXV?$A@$$V@@@Z",
        "void __cdecl f(class A<>)",
        "void f(class A<>)",
        "f")]
    [InlineData("?f@@YAXV?$A@$S@@@Z",
        "void __cdecl f(class A<>)",
        "void f(class A<>)",
        "f")]
    [InlineData("?f@@YAXV?$A@$$A6AXXZ@@@Z",
        "void __cdecl f(class A<void __cdecl(void)>)",
        "void f(class A<void (void)>)",
        "f")]
    [InlineData("?f@@YAXV?$A@$$CBH@@@Z",
        "void __cdecl f(class A<int const >)",
        "void f(class A<int const >)",
        "f")]
    [InlineData("?f@@YAXV?$A@$$CBPEAH@@@Z",
        "void __cdecl f(class A<int * __ptr64 const >)",
        "void f(class A<int * const >)",
        "f")]
    [InlineData("?f@@YAXV?$A@$$CBQ6AXXZ@@@Z",
        "void __cdecl f(class A<void (__cdecl*const )(void)>)",
        "void f(class A<void (*const )(void)>)",
        "f")]
    [InlineData("?f@@YAXV?$A@$$BY01H@@@Z",
        "void __cdecl f(class A<int [2]>)",
        "void f(class A<int [2]>)",
        "f")]
    [InlineData("?f@?$A@V?$B@V?$C@H@@@@@@QEAAXXZ",
        "public: void __cdecl A<class B<class C<int> > >::f(void) __ptr64",
        "public: void A<class B<class C<int> > >::f(void)",
        "A<B<C<int> > >::f")]
    [InlineData("?Create@Factory@@SA?AV?$unique_ptr@VWidget@@U?$default_delete@VWidget@@@std@@@std@@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@3@@Z",
        "public: static class std::unique_ptr<class Widget,struct std::default_delete<class Widget> > __cdecl Factory::Create(class std::basic_string<char,struct std::char_traits<char>,class std::allocator<char> > const & __ptr64)",
        "public: static class std::unique_ptr<class Widget,struct std::default_delete<class Widget> > Factory::Create(class std::basic_string<char,struct std::char_traits<char>,class std::allocator<char> > const &)",
        "Factory::Create")]
    [InlineData("??$Invoke@V<lambda_1>@?1??Run@Task@@QEAAXXZ@@detail@@YAXAEAV<lambda_1>@?1??Run@Task@@QEAAXXZ@@Z",
        "void __cdecl detail::Invoke<class `public: void __cdecl Task::Run(void) __ptr64'::`2'::<lambda_1> >(class `public: void __cdecl Task::Run(void) __ptr64'::`2'::<lambda_1> & __ptr64)",
        "void detail::Invoke<class `public: void Task::Run(void)'::`2'::<lambda_1> >(class `public: void Task::Run(void)'::`2'::<lambda_1> &)",
        "detail::Invoke<`Task::Run'::`2'::<lambda_1> >")]
    [InlineData("?Visit@?A0x12ab34cd@@YAXAEAVNode@@@Z",
        "void __cdecl `anonymous namespace'::Visit(class Node & __ptr64)",
        "void `anonymous namespace'::Visit(class Node &)",
        "`anonymous namespace'::Visit")]
    [InlineData("?f@Bar@?A0xdeadbeef@ns@@QEAAXV12@@Z",
        "public: void __cdecl ns::`anonymous namespace'::Bar::f(class A0xdeadbeef::Bar) __ptr64",
        "public: void ns::`anonymous namespace'::Bar::f(class A0xdeadbeef::Bar)",
        "ns::`anonymous namespace'::Bar::f")]
    [InlineData("?f@?A0x1@@YAXV?$A@H@1@@Z",
        "void __cdecl `anonymous namespace'::f(class A0x1::A<int>)",
        "void `anonymous namespace'::f(class A0x1::A<int>)",
        "`anonymous namespace'::f")]
    public void Templates(string mangled, string complete, string noMsKeywords, string nameOnly) =>
        Check(mangled, complete, noMsKeywords, nameOnly);

    /// <summary>Back-references to names and to argument types, each template with its own tables.</summary>
    [Theory]
    [InlineData("?F@@YAXV?$Pair@VKey@@VKey@@@@@Z",
        "void __cdecl F(class Pair<class Key,class Key>)",
        "void F(class Pair<class Key,class Key>)",
        "F")]
    [InlineData("?Swap@@YAXAEAPEAH0@Z",
        "void __cdecl Swap(int * __ptr64 & __ptr64,int * __ptr64 & __ptr64)",
        "void Swap(int * &,int * &)",
        "Swap")]
    [InlineData("?Concat@@YA?AVText@@AEBV1@0@Z",
        "class Text __cdecl Concat(class Text const & __ptr64,class Text const & __ptr64)",
        "class Text Concat(class Text const &,class Text const &)",
        "Concat")]
    [InlineData("?f@@YAX_N0@Z",
        "void __cdecl f(bool,bool)",
        "void f(bool,bool)",
        "f")]
    [InlineData("?f@@YAXP6AXVFoo@@@Z0@Z",
        "void __cdecl f(void (__cdecl*)(class Foo),class Foo)",
        "void f(void (*)(class Foo),class Foo)",
        "f")]
    [InlineData("?f@@YAXP6AXVFoo@@@Z1@Z",
        "void __cdecl f(void (__cdecl*)(class Foo),void (__cdecl*)(class Foo))",
        "void f(void (*)(class Foo),void (*)(class Foo))",
        "f")]
    [InlineData("?f@@YAXV?$A@VFoo@@0@@@Z",
        "void __cdecl f(class A<class Foo,class Foo>)",
        "void f(class A<class Foo,class Foo>)",
        "f")]
    [InlineData("?f@@YAXV?$A@VFoo@@@@0@Z",
        "void __cdecl f(class A<class Foo>,class A<class Foo>)",
        "void f(class A<class Foo>,class A<class Foo>)",
        "f")]
    [InlineData("?f@@YAXV?$A@$0BA@0@@@Z",
        "void __cdecl f(class A<16,16>)",
        "void f(class A<16,16>)",
        "f")]
    [InlineData("?f@?$A@H@@QEAAXV?$B@H@1@@Z",
        "public: void __cdecl A<int>::f(class A<int>::B<int>) __ptr64",
        "public: void A<int>::f(class A<int>::B<int>)",
        "A<int>::f")]
    [InlineData("?f@?$A@H@ns@@QEAAXV12@V?$B@V12@@2@@Z",
        "?f@?$A@H@ns@@QEAAXV12@V?$B@V12@@2@@Z",
        "?f@?$A@H@ns@@QEAAXV12@V?$B@V12@@2@@Z",
        "ns::A<int>::f")]
    [InlineData("?f@@YAXV?$A@VFoo@@@@V?$B@VFoo@@@@V2@@Z",
        "void __cdecl f(class A<class Foo>,class B<class Foo>,class B<class Foo>)",
        "void f(class A<class Foo>,class B<class Foo>,class B<class Foo>)",
        "f")]
    public void BackReferences(string mangled, string complete, string noMsKeywords, string nameOnly) =>
        Check(mangled, complete, noMsKeywords, nameOnly);

    /// <summary>Pointers, references, rvalue references, cv qualifiers, __ptr64, __restrict and __unaligned.</summary>
    [Theory]
    [InlineData("?f@@YAXAEBQEAH@Z",
        "void __cdecl f(int * __ptr64 const & __ptr64)",
        "void f(int * const &)",
        "f")]
    [InlineData("?f@@YAXAEBPEAH@Z",
        "void __cdecl f(int * __ptr64 const & __ptr64)",
        "void f(int * const &)",
        "f")]
    [InlineData("?f@@YAXAEAQEAH@Z",
        "void __cdecl f(int * __ptr64 & __ptr64)",
        "void f(int * &)",
        "f")]
    [InlineData("?f@@YAXPEDH@Z",
        "void __cdecl f(int const volatile * __ptr64)",
        "void f(int const volatile *)",
        "f")]
    [InlineData("?f@@YAXRECH@Z",
        "void __cdecl f(int volatile * __ptr64 volatile)",
        "void f(int volatile * volatile)",
        "f")]
    [InlineData("?f@@YAXSEDH@Z",
        "void __cdecl f(int const volatile * __ptr64 const volatile)",
        "void f(int const volatile * const volatile)",
        "f")]
    [InlineData("?f@@YAXBEAH@Z",
        "void __cdecl f(int & __ptr64 volatile)",
        "void f(int & volatile)",
        "f")]
    [InlineData("?f@@YAXPEIAH@Z",
        "void __cdecl f(int * __ptr64 __restrict)",
        "void f(int *)",
        "f")]
    [InlineData("?f@@YAXPEFAH@Z",
        "void __cdecl f(int __unaligned * __ptr64)",
        "void f(int *)",
        "f")]
    [InlineData("?f@@YAXPEAPEAPEAH@Z",
        "void __cdecl f(int * __ptr64 * __ptr64 * __ptr64)",
        "void f(int * * *)",
        "f")]
    [InlineData("?f@@YAXPEDPEAH@Z",
        "void __cdecl f(int * __ptr64 const volatile * __ptr64)",
        "void f(int * const volatile *)",
        "f")]
    [InlineData("?Move@Buffer@@QEAAX$$QEAV1@@Z",
        "public: void __cdecl Buffer::Move(class Buffer && __ptr64) __ptr64",
        "public: void Buffer::Move(class Buffer &&)",
        "Buffer::Move")]
    [InlineData("?f@@YAX$$REAH@Z",
        "void __cdecl f(int && __ptr64 volatile)",
        "void f(int && volatile)",
        "f")]
    [InlineData("?f@@YAQEAHXZ",
        "int * __ptr64 __cdecl f(void)",
        "int * f(void)",
        "f")]
    [InlineData("?f@@YA?BVFoo@@XZ",
        "class Foo const __cdecl f(void)",
        "class Foo const f(void)",
        "f")]
    [InlineData("?f@@YA$$CBHXZ",
        "int const __cdecl f(void)",
        "int const f(void)",
        "f")]
    public void Pointers(string mangled, string complete, string noMsKeywords, string nameOnly) =>
        Check(mangled, complete, noMsKeywords, nameOnly);

    /// <summary>Function pointers, member pointers and arrays, where the name sits inside the type.</summary>
    [Theory]
    [InlineData("?Apply@@YAXP6AHHH@ZHH@Z",
        "void __cdecl Apply(int (__cdecl*)(int,int),int,int)",
        "void Apply(int (*)(int,int),int,int)",
        "Apply")]
    [InlineData("?Invoke@Widget@ui@@QEAAXP81@EAAXH@ZH@Z",
        "public: void __cdecl ui::Widget::Invoke(void (__cdecl Widget::*)(int) __ptr64,int) __ptr64",
        "public: void ui::Widget::Invoke(void ( Widget::*)(int),int)",
        "ui::Widget::Invoke")]
    [InlineData("?Offset@@YA_JPEQWidget@ui@@H@Z",
        "__int64 __cdecl Offset(int ui::Widget::* __ptr64)",
        "__int64 Offset(int ui::Widget::*)",
        "Offset")]
    [InlineData("?f@@YAXQ8Foo@@EAAXXZ@Z",
        "void __cdecl f(void (__cdecl Foo::*const)(void) __ptr64)",
        "void f(void ( Foo::*const)(void))",
        "f")]
    [InlineData("?f@@YAXPEBP6AXXZ@Z",
        "void __cdecl f(void (__cdecl*const * __ptr64)(void))",
        "void f(void (*const *)(void))",
        "f")]
    [InlineData("?f@@YAXPEQFoo@@P6AXXZ@Z",
        "void __cdecl f(void (__cdecl*Foo::* __ptr64)(void))",
        "void f(void (*Foo::*)(void))",
        "f")]
    [InlineData("?f@@YAXP8Foo@@EAAP6AXXZXZ@Z",
        "void __cdecl f(void (__cdecl*(__cdecl Foo::*)(void) __ptr64)(void))",
        "void f(void (*( Foo::*)(void))(void))",
        "f")]
    [InlineData("?f@@YAXP6AP6AXXZXZ@Z",
        "void __cdecl f(void (__cdecl*(__cdecl*)(void))(void))",
        "void f(void (*(*)(void))(void))",
        "f")]
    [InlineData("?f@@YAP6AXXZXZ",
        "void (__cdecl*__cdecl f(void))(void)",
        "void (*f(void))(void)",
        "f")]
    [InlineData("?f@@YAXA6AXXZ@Z",
        "void __cdecl f(void (__cdecl&)(void))",
        "void f(void (&)(void))",
        "f")]
    [InlineData("?Fill@@YAXAEAY03M@Z",
        "void __cdecl Fill(float (& __ptr64)[4])",
        "void Fill(float (&)[4])",
        "Fill")]
    [InlineData("?f@@YAXPEAY111H@Z",
        "void __cdecl f(int (* __ptr64)[2][2])",
        "void f(int (*)[2][2])",
        "f")]
    [InlineData("?f@@YAXPEBY01H@Z",
        "void __cdecl f(int (const * __ptr64)[2])",
        "void f(int (const *)[2])",
        "f")]
    [InlineData("?f@@YAXPEQFoo@@Y01H@Z",
        "void __cdecl f(int (Foo::* __ptr64)[2])",
        "void f(int (Foo::*)[2])",
        "f")]
    [InlineData("?f@@YAPEAY01HXZ",
        "int (* __ptr64 __cdecl f(void))[2]",
        "int (* f(void))[2]",
        "f")]
    [InlineData("?f@@YAX$$BY01H@Z",
        "void __cdecl f(int [2])",
        "void f(int [2])",
        "f")]
    [InlineData("?g_grid@@3PAY03HA",
        "int (* g_grid)[4]",
        "int (* g_grid)[4]",
        "g_grid")]
    [InlineData("?fp@@3P6AXXZEA",
        "void (__cdecl* __ptr64 fp)(void)",
        "void (* fp)(void)",
        "fp")]
    [InlineData("?fp@@3PEAP6AXXZEA",
        "void (__cdecl** __ptr64 __ptr64 fp)(void)",
        "void (** fp)(void)",
        "fp")]
    [InlineData("?fp@@3P8Foo@@EBAXXZEQ1@",
        "void (__cdecl Foo::* __ptr64 fp)(void)const __ptr64",
        "void ( Foo::* fp)(void)const ",
        "fp")]
    public void Declarators(string mangled, string complete, string noMsKeywords, string nameOnly) =>
        Check(mangled, complete, noMsKeywords, nameOnly);

    /// <summary>Static members and globals with their storage class.</summary>
    [Theory]
    [InlineData("?Count@Registry@@2HA",
        "public: static int Registry::Count",
        "public: static int Registry::Count",
        "Registry::Count")]
    [InlineData("?x@Foo@@1HA",
        "protected: static int Foo::x",
        "protected: static int Foo::x",
        "Foo::x")]
    [InlineData("?s_instance@Manager@core@@0PEAV12@EA",
        "private: static class core::Manager * __ptr64 __ptr64 core::Manager::s_instance",
        "private: static class core::Manager * core::Manager::s_instance",
        "core::Manager::s_instance")]
    [InlineData("?g_counter@@3IC",
        "unsigned int volatile g_counter",
        "unsigned int volatile g_counter",
        "g_counter")]
    [InlineData("?g_name@@3PEBDEB",
        "char const * __ptr64 const __ptr64 g_name",
        "char const * const g_name",
        "g_name")]
    [InlineData("?x@@3HB",
        "int const x",
        "int const x",
        "x")]
    [InlineData("?x@@3HEB",
        "int const __ptr64 x",
        "int const x",
        "x")]
    [InlineData("?x@@3VFoo@@B",
        "class Foo const x",
        "class Foo const x",
        "x")]
    [InlineData("?x@@3AEBHEB",
        "int const & __ptr64 const __ptr64 x",
        "int const & const x",
        "x")]
    [InlineData("?m@@3PEQFoo@@HEQ1@",
        "int Foo::* __ptr64 __ptr64 m",
        "int Foo::* m",
        "m")]
    [InlineData("?x@?1??f@@YAXXZ@4HA",
        "int `void __cdecl f(void)'::`2'::x",
        "int `void f(void)'::`2'::x",
        "`f'::`2'::x")]
    [InlineData("?s@?1??f@@YAXXZ@4V?$A@H@@A",
        "class A<int> `void __cdecl f(void)'::`2'::s",
        "class A<int> `void f(void)'::`2'::s",
        "`f'::`2'::s")]
    [InlineData("?x@ns@@3V?$A@H@1@A",
        "class ns::A<int> ns::x",
        "class ns::A<int> ns::x",
        "ns::x")]
    public void Data(string mangled, string complete, string noMsKeywords, string nameOnly) =>
        Check(mangled, complete, noMsKeywords, nameOnly);

    /// <summary>Compiler-generated names: virtual tables, RTTI, thunks, string literals, hashed names.</summary>
    [Theory]
    [InlineData("??_7Widget@ui@@6B@",
        "const ui::Widget::`vftable'",
        "const ui::Widget::`vftable'",
        "ui::Widget::`vftable'")]
    [InlineData("??_7Widget@ui@@6BIDrawable@1@@",
        "const ui::Widget::`vftable'{for `ui::IDrawable'}",
        "const ui::Widget::`vftable'{for `ui::IDrawable'}",
        "ui::Widget::`vftable'")]
    [InlineData("??_7Foo@@6BBar@@Baz@@@",
        "const Foo::`vftable'{for `Bar's `Baz'}",
        "const Foo::`vftable'{for `Bar's `Baz'}",
        "Foo::`vftable'")]
    [InlineData("??_7?$A@H@ns@@6B?$B@H@1@@",
        "const ns::A<int>::`vftable'{for `ns::B<int>'}",
        "const ns::A<int>::`vftable'{for `ns::B<int>'}",
        "ns::A<int>::`vftable'")]
    [InlineData("??_8Derived@@7BBase@@@",
        "const Derived::`vbtable'{for `Base'}",
        "const Derived::`vbtable'{for `Base'}",
        "Derived::`vbtable'")]
    [InlineData("??_GWidget@ui@@UEAAPEAXI@Z",
        "public: virtual void * __ptr64 __cdecl ui::Widget::`scalar deleting destructor'(unsigned int) __ptr64",
        "public: virtual void * ui::Widget::`scalar deleting destructor'(unsigned int)",
        "ui::Widget::`scalar deleting destructor'")]
    [InlineData("??_EFoo@@UEAAPEAXI@Z",
        "public: virtual void * __ptr64 __cdecl Foo::`vector deleting destructor'(unsigned int) __ptr64",
        "public: virtual void * Foo::`vector deleting destructor'(unsigned int)",
        "Foo::`vector deleting destructor'")]
    [InlineData("??_DFoo@@QEAAXXZ",
        "public: void __cdecl Foo::`vbase destructor'(void) __ptr64",
        "public: void Foo::`vbase destructor'(void)",
        "Foo::`vbase destructor'")]
    [InlineData("??_FFoo@@QEAAXXZ",
        "public: void __cdecl Foo::`default constructor closure'(void) __ptr64",
        "public: void Foo::`default constructor closure'(void)",
        "Foo::`default constructor closure'")]
    [InlineData("??_SFoo@@6B@",
        "const Foo::`local vftable'",
        "const Foo::`local vftable'",
        "Foo::`local vftable'")]
    [InlineData("??_R0?AVFoo@@@8",
        "class Foo `RTTI Type Descriptor'",
        "class Foo `RTTI Type Descriptor'",
        "Foo `RTTI Type Descriptor'")]
    [InlineData("??_R0PEAVFoo@@@8",
        "class Foo * __ptr64 `RTTI Type Descriptor'",
        "class Foo * `RTTI Type Descriptor'",
        "Foo * __ptr64 `RTTI Type Descriptor'")]
    [InlineData("??_R1A@?0A@EA@Foo@@8",
        "Foo::`RTTI Base Class Descriptor at (0,-1,0,64)'",
        "Foo::`RTTI Base Class Descriptor at (0,-1,0,64)'",
        "Foo::`RTTI Base Class Descriptor at (0,-1,0,64)'")]
    [InlineData("??_R2Foo@@8",
        "Foo::`RTTI Base Class Array'",
        "Foo::`RTTI Base Class Array'",
        "Foo::`RTTI Base Class Array'")]
    [InlineData("??_R3Foo@@8",
        "Foo::`RTTI Class Hierarchy Descriptor'",
        "Foo::`RTTI Class Hierarchy Descriptor'",
        "Foo::`RTTI Class Hierarchy Descriptor'")]
    [InlineData("??_R4Foo@@6B@",
        "const Foo::`RTTI Complete Object Locator'",
        "const Foo::`RTTI Complete Object Locator'",
        "Foo::`RTTI Complete Object Locator'")]
    [InlineData("??_R4Foo@@6BBar@@@",
        "const Foo::`RTTI Complete Object Locator'{for `Bar'}",
        "const Foo::`RTTI Complete Object Locator'{for `Bar'}",
        "Foo::`RTTI Complete Object Locator'")]
    [InlineData("??_C@_0M@ABCDEFGH@hello?5world?$AA@",
        "`string'",
        "`string'",
        "`string'")]
    [InlineData("??_C@_1BA@ABCDEFGH@?$AAh?$AAi@",
        "`string'",
        "`string'",
        "`string'")]
    [InlineData("??@0123456789abcdef0123456789abcdef@",
        "??@0123456789abcdef0123456789abcdef@",
        "??@0123456789abcdef0123456789abcdef@",
        "??@0123456789abcdef0123456789abcdef@")]
    [InlineData("??_B?1??f@@YAXXZ@51",
        "`void __cdecl f(void)'::`2'::`local static guard'{2}'",
        "`void f(void)'::`2'::`local static guard'{2}'",
        "`f'::`2'::`local static guard'")]
    [InlineData("??__Ex@@YAXXZ",
        "void __cdecl `dynamic initializer for 'x''(void)",
        "void `dynamic initializer for 'x''(void)",
        "`dynamic initializer for 'x''")]
    [InlineData("??__E?x@Foo@@2HA@@YAXXZ",
        "void __cdecl `dynamic initializer for 'public: static int Foo::x''(void)",
        "void `dynamic initializer for 'public: static int Foo::x''(void)",
        "`dynamic initializer for 'Foo::x''")]
    [InlineData("?f@Foo@@WBA@EAAXXZ",
        "[thunk]:public: virtual void __cdecl Foo::f`adjustor{16}' (void) __ptr64",
        "[thunk]:public: virtual void Foo::f`adjustor{16}' (void)",
        "Foo::f")]
    [InlineData("?f@Foo@@$4PPPPPPPM@A@EAAXXZ",
        "[thunk]:public: virtual void __cdecl Foo::f`vtordisp{4294967292,0}' (void) __ptr64",
        "[thunk]:public: virtual void Foo::f`vtordisp{4294967292,0}' (void)",
        "Foo::f")]
    [InlineData("??_9Foo@@$BA@AA",
        "[thunk]: __cdecl Foo::`vcall'{0,{flat}}' }'",
        "[thunk]:Foo::`vcall'{0,{flat}}' }'",
        "Foo::`vcall'{0}'")]
    [InlineData("plain_c_function",
        "plain_c_function",
        "plain_c_function",
        "plain_c_function")]
    public void SpecialNames(string mangled, string complete, string noMsKeywords, string nameOnly) =>
        Check(mangled, complete, noMsKeywords, nameOnly);

    [Fact]
    public void EmptyInputFails()
    {
        Assert.Null(MsvcDemangler.Undecorate("", UndecorateFlags.NoMsKeywords));
        Assert.Throws<ArgumentNullException>(() => MsvcDemangler.Undecorate(null!, UndecorateFlags.NameOnly));
    }

    [Theory]
    [InlineData("?f@@YAXVFoo@@V?$A@0@@@Z", "f")]
    [InlineData("?f@@YAPEAVFoo@@0@Z", "f")]
    [InlineData("?f@@YAXV?$A@$D0@@@Z", "f")]
    public void MalformedNamesComeBackUnchanged(string mangled, string nameOnly)
    {
        // dbghelp returns the input when the type part cannot be parsed; the name-only form stops before it.
        Assert.Equal(mangled, MsvcDemangler.Undecorate(mangled, UndecorateFlags.NoMsKeywords));
        Assert.Equal(mangled, MsvcDemangler.Undecorate(mangled, UndecorateFlags.Complete));
        Assert.Equal(nameOnly, MsvcDemangler.Undecorate(mangled, UndecorateFlags.NameOnly));
    }

    [Fact]
    public void DeepNestingDoesNotOverflow()
    {
        const int depth = 1300;
        string pointers = "?f@@YAX" + string.Concat(Enumerable.Repeat("PEA", depth)) + "H@Z";
        string expected = "void f(int" + string.Concat(Enumerable.Repeat(" *", depth)) + ")";
        Assert.Equal(expected, MsvcDemangler.Undecorate(pointers, UndecorateFlags.NoMsKeywords));

        string templates = "?f@@YAXV" + string.Concat(Enumerable.Repeat("?$A@V", 400)) + "Foo@@"
            + string.Concat(Enumerable.Repeat("@@", 400)) + "@Z";
        string? text = MsvcDemangler.Undecorate(templates, UndecorateFlags.NoMsKeywords);
        Assert.StartsWith("void f(class A<class A<", text);
        Assert.EndsWith("> > >)", text);

        // Far beyond any real name: must fail cleanly, not crash.
        string absurd = "?f@@YAX" + string.Concat(Enumerable.Repeat("P6A", 20000)) + "XXZ@Z";
        Assert.NotNull(MsvcDemangler.Undecorate(absurd, UndecorateFlags.NoMsKeywords));
    }

    [Fact]
    public void ParallelCallsAgree()
    {
        string[] names =
        [

            "?Foo@Bar@ns@@QEAAXH@Z",
            "?Get@Widget@ui@@QEBAPEBVNode@2@H@Z",
            "?Update@Widget@ui@@MEAAXM@Z",
            "?Hidden@Widget@ui@@AEAA_NXZ",
            "?f@Foo@@SAXXZ",
            "?f@Foo@@CAXXZ",
            "?f@Foo@@UEBAXXZ",
            "?f@Foo@@QEDAXXZ",
            "?f@Foo@@QEIAAXXZ",
            "?Name@Item@@QEGBAPEBDXZ",
            "?Take@Item@@QEHAA?AV1@XZ",
            "?f@Foo@@QAEXXZ",
            "?f@ns@@YAXXZ",
            "?f@@YGXXZ",
            "?f@@YQXXZ",
            "?f@@YAXZZ",
            "?f@@YAXHZZ",
            "?f@@YAXH@_E",
            "?f@@YAX_W_S_U_Q_J_K_L_M_N@Z",
            "?f@@YAXCDEFGIJKMNO@Z",
            "?f@@YAX_D_E_F_G_H_I@Z",
            "?f@@YAXW4E@@@Z",
            "?f@@YAXW0E@@@Z",
            "?f@@YAXTU@@@Z",
            "?f@@YAX$$TAEA$$T@Z",
            "?f@@YAX_$H@Z",
            "?f@@YMPE$AAVString@System@@XZ",
            "??0Widget@ui@@QEAA@AEBV01@@Z",
            "??1Widget@ui@@UEAA@XZ",
            "??0?$A@H@@QEAA@XZ",
            "??1?$A@H@@QEAA@XZ",
            "??4Foo@@QEAAAEAV0@AEBV0@@Z",
            "??M?$Foo@H@@QEBA_NAEBV0@@Z",
            "??RFoo@@QEAAHH@Z",
            "??AFoo@@QEAAAEAHH@Z",
            "??CFoo@@QEAAPEAV0@XZ",
            "??JFoo@@QEAAHH@Z",
            "??QFoo@@QEAAHH@Z",
            "??_4Foo@@QEAAAEAV0@AEBV0@@Z",
            "??__MFoo@@QEBA?AUstrong_ordering@std@@AEBV0@@Z",
            "??BFoo@@QEBA_NXZ",
            "??B?$A@H@@QEAA?AV?$B@H@@XZ",
            "??$?BH@Foo@@QEAAHXZ",
            "??$?6H@@YAXH@Z",
            "??$?MH@@YA_NHH@Z",
            "??2@YAPEAX_K@Z",
            "??3@YAXPEAX@Z",
            "??_U@YAPEAX_K@Z",
            "??_VFoo@@SAXPEAX@Z",
            "??R<lambda_9f3a>@@QEBA@H@Z",
            "??$Max@H@math@@YAHHH@Z",
            "?Set@?$Array@H$0BA@@@QEAAX_KH@Z",
            "?Tag@?$Holder@$0?5@@QEAAXXZ",
            "?Get@?$Holder@$1?g_value@@3HA@@SAPEAHXZ",
            "?f@@YAXV?$A@$1?f@Foo@@QEAAXXZ@@@Z",
            "?f@@YAXV?$A@$E?x@@3HA@@@Z",
            "?f@@YAXV?$A@$0A@$00$0BA@$0?1$0?BA@$0PPPPPPPP@$0IAAAAAAAAAAAAAAA@@@@Z",
            "?f@@YAXV?$A@$MH0A@@@@Z",
            "?f@@YAXV?$A@$1@@@@Z",
            "?f@@YAXV?$A@$$V@@@Z",
            "?f@@YAXV?$A@$S@@@Z",
            "?f@@YAXV?$A@$$A6AXXZ@@@Z",
            "?f@@YAXV?$A@$$CBH@@@Z",
            "?f@@YAXV?$A@$$CBPEAH@@@Z",
            "?f@@YAXV?$A@$$CBQ6AXXZ@@@Z",
            "?f@@YAXV?$A@$$BY01H@@@Z",
            "?f@?$A@V?$B@V?$C@H@@@@@@QEAAXXZ",
            "?Create@Factory@@SA?AV?$unique_ptr@VWidget@@U?$default_delete@VWidget@@@std@@@std@@AEBV?$basic_string@DU?$char_traits@D@std@@V?$allocator@D@2@@3@@Z",
            "??$Invoke@V<lambda_1>@?1??Run@Task@@QEAAXXZ@@detail@@YAXAEAV<lambda_1>@?1??Run@Task@@QEAAXXZ@@Z",
            "?Visit@?A0x12ab34cd@@YAXAEAVNode@@@Z",
            "?f@Bar@?A0xdeadbeef@ns@@QEAAXV12@@Z",
            "?f@?A0x1@@YAXV?$A@H@1@@Z",
            "?F@@YAXV?$Pair@VKey@@VKey@@@@@Z",
            "?Swap@@YAXAEAPEAH0@Z",
            "?Concat@@YA?AVText@@AEBV1@0@Z",
            "?f@@YAX_N0@Z",
            "?f@@YAXP6AXVFoo@@@Z0@Z",
            "?f@@YAXP6AXVFoo@@@Z1@Z",
            "?f@@YAXV?$A@VFoo@@0@@@Z",
            "?f@@YAXV?$A@VFoo@@@@0@Z",
            "?f@@YAXV?$A@$0BA@0@@@Z",
            "?f@?$A@H@@QEAAXV?$B@H@1@@Z",
            "?f@?$A@H@ns@@QEAAXV12@V?$B@V12@@2@@Z",
            "?f@@YAXV?$A@VFoo@@@@V?$B@VFoo@@@@V2@@Z",
            "?f@@YAXAEBQEAH@Z",
            "?f@@YAXAEBPEAH@Z",
            "?f@@YAXAEAQEAH@Z",
            "?f@@YAXPEDH@Z",
            "?f@@YAXRECH@Z",
            "?f@@YAXSEDH@Z",
            "?f@@YAXBEAH@Z",
            "?f@@YAXPEIAH@Z",
            "?f@@YAXPEFAH@Z",
            "?f@@YAXPEAPEAPEAH@Z",
            "?f@@YAXPEDPEAH@Z",
            "?Move@Buffer@@QEAAX$$QEAV1@@Z",
            "?f@@YAX$$REAH@Z",
            "?f@@YAQEAHXZ",
            "?f@@YA?BVFoo@@XZ",
            "?f@@YA$$CBHXZ",
            "?Apply@@YAXP6AHHH@ZHH@Z",
            "?Invoke@Widget@ui@@QEAAXP81@EAAXH@ZH@Z",
            "?Offset@@YA_JPEQWidget@ui@@H@Z",
            "?f@@YAXQ8Foo@@EAAXXZ@Z",
            "?f@@YAXPEBP6AXXZ@Z",
            "?f@@YAXPEQFoo@@P6AXXZ@Z",
            "?f@@YAXP8Foo@@EAAP6AXXZXZ@Z",
            "?f@@YAXP6AP6AXXZXZ@Z",
            "?f@@YAP6AXXZXZ",
            "?f@@YAXA6AXXZ@Z",
            "?Fill@@YAXAEAY03M@Z",
            "?f@@YAXPEAY111H@Z",
            "?f@@YAXPEBY01H@Z",
            "?f@@YAXPEQFoo@@Y01H@Z",
            "?f@@YAPEAY01HXZ",
            "?f@@YAX$$BY01H@Z",
            "?g_grid@@3PAY03HA",
            "?fp@@3P6AXXZEA",
            "?fp@@3PEAP6AXXZEA",
            "?fp@@3P8Foo@@EBAXXZEQ1@",
            "?Count@Registry@@2HA",
            "?x@Foo@@1HA",
            "?s_instance@Manager@core@@0PEAV12@EA",
            "?g_counter@@3IC",
            "?g_name@@3PEBDEB",
            "?x@@3HB",
            "?x@@3HEB",
            "?x@@3VFoo@@B",
            "?x@@3AEBHEB",
            "?m@@3PEQFoo@@HEQ1@",
            "?x@?1??f@@YAXXZ@4HA",
            "?s@?1??f@@YAXXZ@4V?$A@H@@A",
            "?x@ns@@3V?$A@H@1@A",
            "??_7Widget@ui@@6B@",
            "??_7Widget@ui@@6BIDrawable@1@@",
            "??_7Foo@@6BBar@@Baz@@@",
            "??_7?$A@H@ns@@6B?$B@H@1@@",
            "??_8Derived@@7BBase@@@",
            "??_GWidget@ui@@UEAAPEAXI@Z",
            "??_EFoo@@UEAAPEAXI@Z",
            "??_DFoo@@QEAAXXZ",
            "??_FFoo@@QEAAXXZ",
            "??_SFoo@@6B@",
            "??_R0?AVFoo@@@8",
            "??_R0PEAVFoo@@@8",
            "??_R1A@?0A@EA@Foo@@8",
            "??_R2Foo@@8",
            "??_R3Foo@@8",
            "??_R4Foo@@6B@",
            "??_R4Foo@@6BBar@@@",
            "??_C@_0M@ABCDEFGH@hello?5world?$AA@",
            "??_C@_1BA@ABCDEFGH@?$AAh?$AAi@",
            "??@0123456789abcdef0123456789abcdef@",
            "??_B?1??f@@YAXXZ@51",
            "??__Ex@@YAXXZ",
            "??__E?x@Foo@@2HA@@YAXXZ",
            "?f@Foo@@WBA@EAAXXZ",
            "?f@Foo@@$4PPPPPPPM@A@EAAXXZ",
            "??_9Foo@@$BA@AA",
            "plain_c_function",
        ];
        var expected = names.Select(n => MsvcDemangler.Undecorate(n, UndecorateFlags.NoMsKeywords)).ToArray();
        var results = new string?[names.Length * 50];
        Parallel.For(0, results.Length, i => results[i] = MsvcDemangler.Undecorate(names[i % names.Length], UndecorateFlags.NoMsKeywords));
        for (int i = 0; i < results.Length; i++)
        {
            Assert.Equal(expected[i % names.Length], results[i]);
        }
    }
}
