LAB 3 - DATA SERIALIZATION
XJO - Xarxes per a Jocs Online

WHAT IS IN THIS HANDOUT
  Scenes/S_SerializationTest       a test scene. No sockets: it works with any Lab 2 code
  Scripts/PlayerDTO.cs             the data you send. Do not change it
  Scripts/Serializer.cs            Serialize / Deserialize. The 5 TODOs are here
  Scripts/SerializationTest.cs     fills dummy data and checks your round trips
  Scripts/SerializerSolution.cs    the finished Serializer

IN CLASS
  Open S_SerializationTest and press Play. Every test says PASS, FAIL or SKIPPED.
  Do TODO 1 to 5 in order until everything says PASS.
  Stuck? Disable the GameObject SerializationTest, enable SerializationTestSolution,
  press Play and compare. Its code is in SerializerSolution.cs.

CHALLENGE 3 (in class, if you finish 1 and 2)
  Copy PlayerDTO.cs and Serializer.cs into your Lab 2 lobby project.
  The client sends its PlayerDTO2 on joining, with the type byte first (slide 18).
  The server logs it. Chat and player list keep working as Text messages.

NOTHING TO HAND IN
  Deliverable 3 (mid-term demo) is presented in Lab 4 and starts from this code.
