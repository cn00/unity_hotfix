-- automatically generated by the FlatBuffers compiler, do not modify

-- namespace: common

local flatbuffers = require('flatbuffers')

local Jewery = {} -- the module
local Jewery_mt = {} -- the class metatable

function Jewery.New()
    local o = {}
    setmetatable(o, {__index = Jewery_mt})
    return o
end
function Jewery.GetRootAsJewery(buf, offset)
    local n = flatbuffers.N.UOffsetT:Unpack(buf, offset)
    local o = Jewery.New()
    o:Init(buf, n + offset)
    return o
end
function Jewery_mt:Init(buf, pos)
    self.view = flatbuffers.view.New(buf, pos)
end
function Jewery_mt:Name()
    local o = self.view:Offset(4)
    if o ~= 0 then
        return self.view:String(o + self.view.pos)
    end
end
function Jewery_mt:Damage()
    local o = self.view:Offset(6)
    if o ~= 0 then
        return self.view:Get(flatbuffers.N.Int16, o + self.view.pos)
    end
    return 0
end
function Jewery.Start(builder) builder:StartObject(2) end
function Jewery.AddName(builder, name) builder:PrependUOffsetTRelativeSlot(0, name, 0) end
function Jewery.AddDamage(builder, damage) builder:PrependInt16Slot(1, damage, 0) end
function Jewery.End(builder) return builder:EndObject() end

return Jewery -- return the module