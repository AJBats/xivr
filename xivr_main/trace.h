#pragma once

// uncomment to enable tracing
#define MTR_ENABLED
#include "minitrace.h"

extern bool g_Trace;

#ifdef MTR_ENABLED

#define XIVTR_BEGIN(c, n) if(g_Trace) MTR_BEGIN(c, n)
#define XIVTR_END(c, n) if(g_Trace) MTR_END(c, n)

#else // MTR_ENABLED

#define XIVTR_BEGIN(c, n)
#define XIVTR_END(c, n)

#endif // MTR_ENABLED
