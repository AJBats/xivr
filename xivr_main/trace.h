#pragma once

// Use in conjuction with: 
// https://github.com/hrydgard/minitrace
// 
// Configure minitrace with:
// cmake -DCMAKE_C_FLAGS="-DMTR_COPY_EVENT_CATEGORY_AND_NAME" ..
// This will allow traces from C# xivr_hooks to function correctly at
// the cost of small perf hit per trace block.

// uncomment to enable tracing
#define MTR_ENABLED

#ifdef MTR_ENABLED
#include "minitrace.h"

extern bool g_Trace;

#define XIVTR_BEGIN(c, n) if(g_Trace) MTR_BEGIN(c, n)
#define XIVTR_END(c, n) if(g_Trace) MTR_END(c, n)
#define XIVTR_INSTANT(c, n) if(g_Trace) MTR_INSTANT(c, n)

#else // MTR_ENABLED

#define XIVTR_BEGIN(c, n)
#define XIVTR_END(c, n)
#define XIVTR_INSTANT(c, n)

#endif // MTR_ENABLED
