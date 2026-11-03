-- The Luau API the demo app gives its scripts. Documentation is in demo.docs.json.

declare function log(message: string): ()

declare extern type Device with
    name: string
    function readTemperature(self): number
    function setLed(self, on: boolean): ()
    function read(self, register: number, length: number): buffer
end

declare device: Device

declare limits: {
    maxTemperature: number,
}
