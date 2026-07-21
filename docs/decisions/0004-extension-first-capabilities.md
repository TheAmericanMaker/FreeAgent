# 0004: Extension-first capabilities

Status: Accepted

Optional or high-risk capabilities should land behind extension and adapter seams rather than being hardwired into the kernel.

Core seams include provider adapters, tool registry, permission engine, persistence, renderer/delivery surfaces, extension registration, and future SDK/RPC modes. This keeps the kernel small while preserving room for the full FreeAgent product.
