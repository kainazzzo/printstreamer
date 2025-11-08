# Quick Reference: PrintStreamer Data Flow Architecture

**Last Updated:** November 7, 2025

---

## 📚 Documentation Files

Three new documentation files have been created:

| File | Purpose | Audience |
|------|---------|----------|
| `DATAFLOW_ARCHITECTURE.md` | Complete architecture specification | Architects, Reviewers |
| `IMPLEMENTATION_PLAN.md` | Step-by-step implementation guide | Developers |
| `DOCUMENTATION_SUMMARY.md` | Overview of both documents | Everyone |

---

## 🔍 Quick Lookup

### I want to understand the architecture
→ Start with **DATAFLOW_ARCHITECTURE.md** - Read the Overview and Proposed Architecture sections

### I need to implement the changes
→ Follow **IMPLEMENTATION_PLAN.md** - Start with Phase 1

### I need to test something
→ Check **IMPLEMENTATION_PLAN.md** - Testing Strategy section

### I need to know what changed
→ See **IMPLEMENTATION_PLAN.md** - Changes Summary at the beginning

### Something broke, how do I rollback?
→ **IMPLEMENTATION_PLAN.md** - Rollback Plan section

### I need to monitor the pipeline
→ **DATAFLOW_ARCHITECTURE.md** - Error Handling & Resilience section

---

## 🎯 The Architecture in 30 Seconds

**Current System:**
```
Webcam → /stream → OverlayMjpegStreamer → FfmpegStreamer → YouTube
                  (overlays applied)     (audio mixed)
```

**Proposed System:**
```
Webcam → /stream/source ┐
                        ├→ OverlayMjpegStreamer → /stream/overlay ┐
    Audio Queue → /stream/audio ────────────────────────────────┤
                                                                 ├→ MixStreamer → /stream/mix → FfmpegStreamer → YouTube
                                                                 │
                      (text overlays)                           (video+audio)
```

**Benefits:**
- 4 independent HTTP endpoints for debugging
- Each stage is self-contained and testable
- Failures don't cascade
- Better monitoring and observability

---

## 📊 The 4 New Endpoints

| Endpoint | Format | Purpose | Input |
|----------|--------|---------|-------|
| `/stream/source` | MJPEG | Raw webcam stream | Physical camera |
| `/stream/overlay` | MJPEG | Video with text overlays | `/stream/source` |
| `/stream/audio` | MP3 | Live audio from queue | Audio files |
| `/stream/mix` | MP4 | Video+audio combined | `/stream/overlay` + `/stream/audio` |

---

## ⚙️ Implementation Phases

### Phase 1: Core Pipeline (Easy - 1 hour)
✅ Add `/stream/source` and `/stream/audio` routes
✅ Update OverlayMjpegStreamer to use `/stream/source`
✅ Build and verify

### Phase 2: Mix Pipeline (Medium - 1 hour)
✅ Create new `MixStreamer` class
✅ Add `/stream/mix` route
✅ Test mix endpoint

### Phase 3: YouTube Integration (Medium - 1 hour)
✅ Update FfmpegStreamer to read from `/stream/mix`
✅ Update StreamService
✅ Test YouTube broadcast

### Phase 4: Cleanup (Easy - variable)
✅ Update logging
✅ Add debug endpoints
✅ Update docs

---

## 🧪 Testing Each Stage

```bash
# Stage 1: Webcam source
curl http://localhost:8080/stream/source -o test1.mjpeg

# Stage 2: Overlay video
timeout 3 curl http://localhost:8080/stream/overlay -o test2.mjpeg

# Stage 3: Audio stream
curl http://localhost:8080/stream/audio -o test3.mp3

# Stage 4: Mixed output
timeout 5 curl http://localhost:8080/stream/mix -o test4.mp4

# Verify each file
file test*.{mjpeg,mp3,mp4}
```

---

## 🚀 Implementation Checklist

### Before Starting
- [ ] Read DATAFLOW_ARCHITECTURE.md - Overview section
- [ ] Review IMPLEMENTATION_PLAN.md - Understand all phases
- [ ] Backup current code (git commit)

### Phase 1
- [ ] Add `/stream/source` route
- [ ] Add `/stream/audio` route
- [ ] Update OverlayMjpegStreamer source URL
- [ ] Build and test
- [ ] Verify all endpoints respond

### Phase 2
- [ ] Create `Streamers/MixStreamer.cs`
- [ ] Add `/stream/mix` route
- [ ] Build and test
- [ ] Verify MixStreamer works

### Phase 3
- [ ] Update FfmpegStreamer
- [ ] Update StreamService
- [ ] Build and test
- [ ] Test YouTube broadcast

### Phase 4
- [ ] Update logging
- [ ] Add debug endpoints
- [ ] Build final version
- [ ] Run deployment checklist

---

## 📋 Key Changes at a Glance

| File | Change | Lines | Priority |
|------|--------|-------|----------|
| Program.cs | Add 3 routes | ~30 | HIGH |
| OverlayMjpegStreamer.cs | Update source URL | ~5 | HIGH |
| StreamService.cs | Update source URL | ~5 | MEDIUM |
| FfmpegStreamer.cs | Add /stream/mix check | ~10 | MEDIUM |
| MixStreamer.cs | Create new | ~200 | HIGH |

**Total:** ~250 lines of code changes, 1 new file

---

## 🔧 Configuration

No configuration changes required! All endpoints are hardcoded to `localhost:8080`:
- `/stream/source` ← Always from WebCamManager
- `/stream/audio` ← Always from AudioBroadcastService
- `/stream/overlay` ← Always from OverlayMjpegStreamer
- `/stream/mix` ← Always from MixStreamer

---

## ⚠️ Important Notes

1. **Backward Compatible** - No breaking changes, `/stream` still works
2. **Fallback Support** - Black image fallback works at all stages
3. **Error Handling** - All fixed to handle "response already started" errors (✅ Already fixed in this session)
4. **Audio Optional** - Mix works with or without audio

---

## 📞 Troubleshooting

| Problem | Check |
|---------|-------|
| `/stream/mix` times out | Is `/stream/overlay` working? Is `/stream/audio` working? |
| No audio in mix | Is `Audio:Enabled` set to true? |
| Overlay text missing | Check OverlayTextService logs |
| YouTube broadcast fails | Verify `/stream/mix` works first |
| Black image instead of camera | Is camera online? Use `/api/camera/on` |

---

## 📖 Full Documentation Access

**Read online or in editor:**
```bash
# View in editor
code DATAFLOW_ARCHITECTURE.md
code IMPLEMENTATION_PLAN.md
code DOCUMENTATION_SUMMARY.md

# Or in terminal
cat DATAFLOW_ARCHITECTURE.md | less
cat IMPLEMENTATION_PLAN.md | less
```

---

## 🎓 Learning Path

1. **5 min:** Read this Quick Reference
2. **15 min:** Skim DATAFLOW_ARCHITECTURE.md - Proposed Architecture section
3. **30 min:** Read IMPLEMENTATION_PLAN.md - Phase 1 in detail
4. **60 min:** Implement Phase 1
5. **30 min:** Test and verify
6. **Repeat** for Phases 2-3

---

## ✅ Success Looks Like

```
✓ /stream/source responds with MJPEG
✓ /stream/overlay responds with overlayed MJPEG
✓ /stream/audio responds with MP3
✓ /stream/mix responds with MP4
✓ YouTube broadcast works via /stream/mix
✓ Audio is synchronized with video
✓ Black fallback works when camera is offline
✓ No errors in application logs
✓ Performance acceptable (< 5s latency to YouTube)
```

---

## 🚢 Ready to Deploy

When all phases are complete:

1. ✅ Run full build: `dotnet build`
2. ✅ Run tests: `dotnet test`
3. ✅ Check logs for errors
4. ✅ Test all endpoints: `/stream/{source,overlay,audio,mix}`
5. ✅ Test YouTube broadcast
6. ✅ Run deployment checklist from IMPLEMENTATION_PLAN.md
7. ✅ Commit to git with detailed message
8. ✅ Deploy to production

---

**Questions?** Refer to the full documentation files.  
**Ready to start?** Go to IMPLEMENTATION_PLAN.md Phase 1.

---

*Generated: November 7, 2025*  
*Status: Ready for Implementation* ✅
