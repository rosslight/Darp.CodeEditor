--- Writes a line to the script log.
declare function log(message: string): ()

declare extern type Device with
    name: string
    function readTemperature(self): number
    function setLed(self, on: boolean): ()
end

declare device: Device

declare limits: {
    maxTemperature: number,
}
